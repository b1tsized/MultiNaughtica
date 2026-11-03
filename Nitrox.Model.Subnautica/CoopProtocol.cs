using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using BinaryPack.Attributes;
using Nitrox.Model.Core;
using Nitrox.Model.Packets;

namespace Nitrox.Model.Subnautica;

/// <summary>
///     What a client and a server of this co-op fork must share to talk to each other, and the check that enforces it before a single packet is read.
/// </summary>
/// <remarks>
///     Stock Nitrox only compares <c>Major.Minor</c> in the session policy, which arrives after the first packets were already deserialized. That is not enough here: the packet
///     union is ordered by type name, so every added packet shifts the index of the packets after it, and layouts change without a new type (a new property on an existing packet),
///     so a stock client and this fork cannot even parse each other's policy request. The gate is therefore the connection key of the transport (LiteNetLib refuses a mismatching key
///     before any packet is read): stock Nitrox sends <c>nitrox</c>, this fork sends <see cref="ConnectionKey" />, which holds the co-op version and a fingerprint of the serialized
///     layout of every packet, entity and metadata type and of every Nitrox type they refer to. Two builds of the same source always agree; a change of the wire format separates the
///     builds without anybody having to remember to bump a number. What the fingerprint cannot see (a change of behaviour that leaves the layouts alone, a change inside a type
///     of another assembly) is covered by <c>CoopBuild</c> in <c>Directory.Build.props</c>, which is raised for every zip that is given to somebody.
/// </remarks>
public static class CoopProtocol
{
    /// <summary>
    ///     The connection key of stock Nitrox.
    /// </summary>
    public const string STOCK_CONNECTION_KEY = "nitrox";

    private const string KEY_PREFIX = "nitrox-coop/";
    private static string? fingerprint;

    /// <summary>
    ///     Twelve hex digits that change whenever the serialized layout of a packet, entity or metadata type (or of a Nitrox type they refer to) changes.
    /// </summary>
    public static string Fingerprint => fingerprint ??= ComputeFingerprint(FindSerializedTypes());

    public static string ConnectionKey => $"{KEY_PREFIX}{NitroxEnvironment.CoopVersion}/{Fingerprint}";

    /// <summary>
    ///     The build this process runs, in words (for logs and error messages).
    /// </summary>
    public static string Describe() => DescribeKey(ConnectionKey);

    /// <summary>
    ///     Why a connection that sent <paramref name="sentKey" /> to a server that wants <see cref="ConnectionKey" /> is refused, for the log of the server.
    /// </summary>
    public static string ExplainRefusal(string? sentKey)
    {
        // The key is whatever the sender put in its request: cut, and with its control characters shown as '?' so that a line break cannot forge a line of the server log
        string sent = string.IsNullOrEmpty(sentKey) ? "no key" : $"'{new string((sentKey!.Length > 80 ? sentKey.Substring(0, 80) : sentKey).Select(c => char.IsControl(c) ? '?' : c).ToArray())}'";
        if (sentKey == STOCK_CONNECTION_KEY)
        {
            return $"it sent {sent}, which is what stock Nitrox sends: that player has to install the co-op build ({Describe()}) of this server";
        }
        if (sentKey != null && sentKey.StartsWith(KEY_PREFIX, StringComparison.Ordinal))
        {
            return $"it sent {sent}, a different co-op build; this server is {Describe()} ({ConnectionKey}), both sides have to use the same zip";
        }
        return $"it sent {sent}, which is not a Nitrox key; this server only accepts {ConnectionKey}";
    }

    /// <summary>
    ///     What the player sees when the server refused the connection. <paramref name="serverKey" /> is what the server named as its own key (it is empty when the
    ///     server is a stock Nitrox, which names nothing).
    /// </summary>
    public static string DescribeRefusal(string? serverKey)
    {
        string server = string.IsNullOrEmpty(serverKey)
            ? "The server is not running this co-op build (it is probably stock Nitrox or an older build)"
            : $"The server runs another build: {DescribeKey(serverKey!)}";
        return $"The server refused the connection. {server}. This game is {Describe()}. Everyone has to use the same co-op zip.";
    }

    internal static string DescribeKey(string key)
    {
        if (!key.StartsWith(KEY_PREFIX, StringComparison.Ordinal))
        {
            return $"'{key}'";
        }

        string[] parts = key.Substring(KEY_PREFIX.Length).Split('/');
        return parts.Length == 2 ? $"co-op {parts[0]} (protocol {parts[1]})" : $"co-op '{key.Substring(KEY_PREFIX.Length)}'";
    }

    /// <summary>
    ///     Hashes the serialized layout of the types: for each type its name, its base type, and its serialized members with their types (enums: their underlying type, names and
    ///     values), sorted, without assembly names, so the Mono client and the .NET server agree.
    /// </summary>
    internal static string ComputeFingerprint(IEnumerable<Type> types)
    {
        List<string> lines = types.Select(DescribeType).ToList();
        lines.Sort(StringComparer.Ordinal);
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return BitConverter.ToString(hash, 0, 6).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    ///     One line that names a type and everything about it that is serialized.
    /// </summary>
    internal static string DescribeType(Type type)
    {
        StringBuilder line = new();
        line.Append(TypeName(type)).Append(" : ").Append(type.BaseType == null ? "" : TypeName(type.BaseType)).Append(" {");
        if (type.IsEnum)
        {
            line.Append("underlying=").Append(TypeName(Enum.GetUnderlyingType(type))).Append(';');
            foreach (string name in Enum.GetNames(type).OrderBy(name => name, StringComparer.Ordinal))
            {
                line.Append(name).Append('=').Append(Convert.ToInt64(Enum.Parse(type, name))).Append(';');
            }
        }
        else
        {
            line.Append(string.Join(";", SerializedMembers(type).Select(member => $"{member.Name}:{TypeName(member.Type)}").OrderBy(text => text, StringComparer.Ordinal)));
        }
        return line.Append('}').ToString();
    }

    /// <summary>
    ///     The Nitrox types of the two model assemblies that travel over the wire or into the save: the packets, enums and attributed data types, and every Nitrox type that one
    ///     of them refers to (as a base type, as the type of a serialized member, as an element or a generic argument of such a type), because a change in a type that is
    ///     only used as a member is as much a change of the wire format as one in the packet itself.
    /// </summary>
    internal static IEnumerable<Type> FindSerializedTypes()
    {
        Assembly[] assemblies = new[] { typeof(Packet).Assembly, typeof(CoopProtocol).Assembly }.Distinct().ToArray();
        HashSet<Type> found = [];
        Queue<Type> pending = new();

        void Add(Type type)
        {
            if (found.Add(type))
            {
                pending.Enqueue(type);
            }
        }

        foreach (Assembly assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(type => type != null).ToArray()!;
            }

            foreach (Type type in types)
            {
                if ((type.IsPublic || type.IsNestedPublic) && type.Namespace != null && type.Namespace.StartsWith("Nitrox.", StringComparison.Ordinal) &&
                    !type.IsGenericTypeDefinition && IsSerialized(type))
                {
                    Add(type);
                }
            }
        }

        while (pending.Count > 0)
        {
            Type type = pending.Dequeue();
            foreach (Type referenced in ReferencedTypes(type))
            {
                if (IsNitroxType(referenced, assemblies))
                {
                    Add(referenced);
                }
            }
        }
        return found;
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        if (type.BaseType != null)
        {
            foreach (Type referenced in Unwrap(type.BaseType))
            {
                yield return referenced;
            }
        }
        if (type.IsEnum)
        {
            yield break;
        }
        foreach ((string _, Type memberType) in SerializedMembers(type))
        {
            foreach (Type referenced in Unwrap(memberType))
            {
                yield return referenced;
            }
        }
    }

    /// <summary>
    ///     The types a type expression is made of: the element of an array, the generic definition and the arguments of a generic type.
    /// </summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.IsArray || type.IsByRef)
        {
            foreach (Type element in Unwrap(type.GetElementType()!))
            {
                yield return element;
            }
            yield break;
        }
        if (type.IsGenericParameter)
        {
            yield break;
        }
        if (type.IsGenericType)
        {
            yield return type.IsGenericTypeDefinition ? type : type.GetGenericTypeDefinition();
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type inner in Unwrap(argument))
                {
                    yield return inner;
                }
            }
            yield break;
        }
        yield return type;
    }

    private static bool IsNitroxType(Type type, Assembly[] assemblies)
    {
        return type.Namespace != null && type.Namespace.StartsWith("Nitrox.", StringComparison.Ordinal) && assemblies.Contains(type.Assembly) &&
               !type.IsDefined(typeof(CompilerGeneratedAttribute), false);
    }

    /// <summary>
    ///     The members of a type that the serializer writes: public instance properties and fields, and non-public ones that are marked with <see cref="SerializableMemberAttribute" />
    ///     (<c>NitroxId</c> keeps its value in a private property), except what is marked with <see cref="IgnoredMemberAttribute" />. <c>[IgnoreDataMember]</c> only hides a member
    ///     from the save formats, not from BinaryPack, so it does not take a member out here.
    /// </summary>
    private static IEnumerable<(string Name, Type Type)> SerializedMembers(Type type)
    {
        const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (PropertyInfo property in type.GetProperties(FLAGS))
        {
            if (property.GetMethod != null && property.GetIndexParameters().Length == 0 && IsSerializedMember(property, property.GetMethod.IsPublic))
            {
                yield return (property.Name, property.PropertyType);
            }
        }
        foreach (FieldInfo field in type.GetFields(FLAGS))
        {
            if (IsSerializedMember(field, field.IsPublic))
            {
                yield return (field.Name, field.FieldType);
            }
        }
    }

    private static bool IsSerializedMember(MemberInfo member, bool isPublic)
    {
        if (member.IsDefined(typeof(IgnoredMemberAttribute), true))
        {
            return false;
        }
        return isPublic ? !member.IsDefined(typeof(CompilerGeneratedAttribute), false) : member.IsDefined(typeof(SerializableMemberAttribute), true);
    }

    private static bool IsSerialized(Type type)
    {
        return typeof(Packet).IsAssignableFrom(type) ||
               type.IsEnum ||
               type.IsDefined(typeof(SerializableAttribute), false) ||
               type.IsDefined(typeof(DataContractAttribute), false);
    }

    private static string TypeName(Type type)
    {
        if (type.IsArray)
        {
            return $"{TypeName(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
        }
        if (type.IsGenericParameter)
        {
            return type.Name;
        }
        if (type.IsGenericType)
        {
            string definition = type.GetGenericTypeDefinition().FullName ?? type.Name;
            int tick = definition.IndexOf('`');
            return $"{(tick >= 0 ? definition.Substring(0, tick) : definition)}<{string.Join(",", type.GetGenericArguments().Select(TypeName))}>";
        }
        return type.FullName ?? type.Name;
    }
}
