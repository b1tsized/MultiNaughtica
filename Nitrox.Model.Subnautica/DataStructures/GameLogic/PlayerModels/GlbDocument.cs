using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Nitrox.Model.Subnautica.DataStructures.GameLogic.PlayerModels;

/// <summary>A GLB file that cannot be read: it is not a GLB, it is damaged, or it uses something that the reader does not support (a feature the custom player models do not need).</summary>
public sealed class GlbException : Exception
{
    public GlbException(string message) : base(message)
    {
    }
}

/// <summary>What an accessor of a glTF file says about its data (see <see cref="GlbDocument.GetAccessor" />).</summary>
public sealed class GlbAccessor
{
    public GlbAccessor(int componentType, int count, int components, bool normalized, int byteOffset, int byteStride, float[]? min, float[]? max)
    {
        ComponentType = componentType;
        Count = count;
        Components = components;
        Normalized = normalized;
        ByteOffset = byteOffset;
        ByteStride = byteStride;
        Min = min;
        Max = max;
    }

    /// <summary>The glTF component type: 5120 byte, 5121 unsigned byte, 5122 short, 5123 unsigned short, 5125 unsigned int, 5126 float.</summary>
    public int ComponentType { get; }

    public int Count { get; }

    /// <summary>1 for SCALAR, 2, 3 or 4 for VEC2 to VEC4, 4, 9 or 16 for MAT2 to MAT4.</summary>
    public int Components { get; }

    public bool Normalized { get; }

    /// <summary>Where the first element starts in the binary chunk.</summary>
    public int ByteOffset { get; }

    /// <summary>Bytes from one element to the next (the size of an element when the data is tightly packed).</summary>
    public int ByteStride { get; }

    public float[]? Min { get; }

    public float[]? Max { get; }
}

/// <summary>
/// A glTF 2.0 binary (.glb) file for the custom player models (docs/coop/PLAYER_MODELS.md): the JSON of the scene and the binary chunk that holds the vertices, the animations and the images. A small reader of
/// our own for the subset that the models use (one buffer, no sparse accessors, no extensions): the files come from one exporter (Meshy through Blender), and nothing here depends on the game, so it is tested
/// against the real file and a synthetic one.
/// </summary>
public sealed class GlbDocument
{
    public const uint MAGIC = 0x46546C67;

    /// <summary>The most values (count times components) one accessor may have: 64 MB of floats, far above what a player model holds.</summary>
    public const int MAX_ACCESSOR_VALUES = 16_777_216;
    private const uint CHUNK_JSON = 0x4E4F534A;
    private const uint CHUNK_BIN = 0x004E4942;

    private readonly byte[] data;
    private readonly int binOffset;
    private readonly int binLength;

    private GlbDocument(byte[] data, JObject json, int binOffset, int binLength)
    {
        this.data = data;
        Json = json;
        this.binOffset = binOffset;
        this.binLength = binLength;
    }

    /// <summary>The JSON chunk: the scene, the nodes, the accessors.</summary>
    public JObject Json { get; }

    public static GlbDocument Load(string path) => Parse(File.ReadAllBytes(path));

    public static GlbDocument Parse(byte[] glb)
    {
        if (glb == null || glb.Length < 20)
        {
            throw new GlbException("the file is too short to be a GLB");
        }
        if (ReadUInt32(glb, 0) != MAGIC)
        {
            throw new GlbException("not a GLB file (the glTF magic is missing)");
        }
        uint version = ReadUInt32(glb, 4);
        if (version != 2)
        {
            throw new GlbException($"GLB version {version} is not supported (version 2 only)");
        }
        long declaredLength = ReadUInt32(glb, 8);
        if (declaredLength > glb.Length)
        {
            throw new GlbException($"the file is cut short: it says {declaredLength} bytes and has {glb.Length}");
        }

        JObject? json = null;
        int binOffset = 0;
        int binLength = 0;
        long position = 12;
        while (position + 8 <= declaredLength)
        {
            long chunkLength = ReadUInt32(glb, (int)position);
            uint chunkType = ReadUInt32(glb, (int)position + 4);
            long start = position + 8;
            if (start + chunkLength > glb.Length)
            {
                throw new GlbException("a chunk of the GLB runs past the end of the file");
            }
            if (chunkType == CHUNK_JSON && json == null)
            {
                try
                {
                    json = JObject.Parse(Encoding.UTF8.GetString(glb, (int)start, (int)chunkLength));
                }
                catch (Exception exception) when (exception is not GlbException)
                {
                    throw new GlbException($"the JSON chunk cannot be read: {exception.Message}");
                }
            }
            else if (chunkType == CHUNK_BIN && binLength == 0)
            {
                binOffset = (int)start;
                binLength = (int)chunkLength;
            }
            position = start + chunkLength;
        }
        if (json == null)
        {
            throw new GlbException("the GLB has no JSON chunk");
        }
        string? assetVersion = (string?)json["asset"]?["version"];
        if (assetVersion != "2.0")
        {
            throw new GlbException($"glTF asset version '{assetVersion}' is not supported (2.0 only)");
        }
        if (json["extensionsRequired"] is JArray { Count: > 0 } required)
        {
            throw new GlbException($"the file requires glTF extensions that the reader does not support: {string.Join(", ", required)}");
        }
        if (json["buffers"] is JArray buffers && buffers.Count > 0 && buffers[0]?["uri"] != null)
        {
            throw new GlbException("the buffer of the GLB is an external file, which the reader does not support");
        }
        return new GlbDocument(glb, json, binOffset, binLength);
    }

    /// <summary>The items of a top level array of the JSON ("nodes", "accessors"...): empty when the file has none.</summary>
    public JArray Array(string name) => Json[name] as JArray ?? new JArray();

    public GlbAccessor GetAccessor(int index)
    {
        JArray accessors = Array("accessors");
        if (index < 0 || index >= accessors.Count)
        {
            throw new GlbException($"accessor {index} does not exist");
        }
        JObject accessor = (JObject)accessors[index];
        if (accessor["sparse"] != null)
        {
            throw new GlbException($"accessor {index} is sparse, which the reader does not support");
        }
        int? viewIndex = (int?)accessor["bufferView"];
        if (viewIndex == null)
        {
            throw new GlbException($"accessor {index} has no buffer view");
        }
        JArray views = Array("bufferViews");
        if (viewIndex < 0 || viewIndex >= views.Count)
        {
            throw new GlbException($"buffer view {viewIndex} does not exist");
        }
        JObject view = (JObject)views[viewIndex.Value];
        int componentType = (int?)accessor["componentType"] ?? 0;
        int components = ComponentsOf((string?)accessor["type"]);
        int count = (int?)accessor["count"] ?? 0;
        int elementSize = ComponentSize(componentType) * components;
        int stride = (int?)view["byteStride"] ?? 0;
        if (stride == 0)
        {
            stride = elementSize;
        }
        long offset = (long)((int?)view["byteOffset"] ?? 0) + ((int?)accessor["byteOffset"] ?? 0);
        // Negative numbers slipped past the end-of-chunk check below and then read outside the chunk or allocated a negative array
        if (count < 0 || stride < 0 || offset < 0)
        {
            throw new GlbException($"accessor {index} has a negative count, offset or stride");
        }
        // The reader allocates every value of an accessor at once: a file that asks for more must not make the game allocate gigabytes
        if ((long)count * components > MAX_ACCESSOR_VALUES)
        {
            throw new GlbException($"accessor {index} has {(long)count * components} values, more than the {MAX_ACCESSOR_VALUES} the reader accepts");
        }
        long lastByte = count == 0 ? offset : offset + (long)(count - 1) * stride + elementSize;
        if (lastByte > binLength)
        {
            throw new GlbException($"accessor {index} reads past the end of the binary chunk");
        }
        // offset is at most binLength here, so it fits an int
        return new GlbAccessor(componentType, count, components, (bool?)accessor["normalized"] ?? false, (int)offset, stride, FloatsOf(accessor["min"]), FloatsOf(accessor["max"]));
    }

    /// <summary>All the values of an accessor as floats (count times components of them); integer components are converted, normalised ones to 0..1 (or -1..1).</summary>
    public float[] ReadFloats(int accessorIndex)
    {
        GlbAccessor accessor = GetAccessor(accessorIndex);
        float[] values = new float[accessor.Count * accessor.Components];
        int size = ComponentSize(accessor.ComponentType);
        for (int element = 0; element < accessor.Count; element++)
        {
            int at = binOffset + accessor.ByteOffset + element * accessor.ByteStride;
            for (int component = 0; component < accessor.Components; component++)
            {
                values[element * accessor.Components + component] = ReadComponent(at + component * size, accessor.ComponentType, accessor.Normalized);
            }
        }
        return values;
    }

    /// <summary>The values of a scalar accessor of indices (unsigned byte, short or int).</summary>
    public int[] ReadIndices(int accessorIndex)
    {
        GlbAccessor accessor = GetAccessor(accessorIndex);
        if (accessor.Components != 1 || (accessor.ComponentType != 5121 && accessor.ComponentType != 5123 && accessor.ComponentType != 5125))
        {
            throw new GlbException($"accessor {accessorIndex} is not a list of indices");
        }
        int[] values = new int[accessor.Count];
        for (int element = 0; element < accessor.Count; element++)
        {
            int at = binOffset + accessor.ByteOffset + element * accessor.ByteStride;
            values[element] = accessor.ComponentType switch
            {
                5121 => data[at],
                5123 => data[at] | (data[at + 1] << 8),
                _ => (int)ReadUInt32(data, at)
            };
        }
        return values;
    }

    /// <summary>The joints of a vertex accessor (JOINTS_0): four of them per vertex, as unsigned bytes or shorts.</summary>
    public ushort[] ReadJoints(int accessorIndex)
    {
        GlbAccessor accessor = GetAccessor(accessorIndex);
        if (accessor.Components != 4 || (accessor.ComponentType != 5121 && accessor.ComponentType != 5123))
        {
            throw new GlbException($"accessor {accessorIndex} is not a set of four joints per vertex");
        }
        ushort[] values = new ushort[accessor.Count * 4];
        int size = ComponentSize(accessor.ComponentType);
        for (int element = 0; element < accessor.Count; element++)
        {
            int at = binOffset + accessor.ByteOffset + element * accessor.ByteStride;
            for (int component = 0; component < 4; component++)
            {
                int position = at + component * size;
                values[element * 4 + component] = accessor.ComponentType == 5121 ? data[position] : (ushort)(data[position] | (data[position + 1] << 8));
            }
        }
        return values;
    }

    /// <summary>The bytes of an embedded image (a PNG or a JPEG) of the file.</summary>
    public byte[] ReadImage(int imageIndex)
    {
        JArray images = Array("images");
        if (imageIndex < 0 || imageIndex >= images.Count)
        {
            throw new GlbException($"image {imageIndex} does not exist");
        }
        JObject image = (JObject)images[imageIndex];
        int? viewIndex = (int?)image["bufferView"];
        if (viewIndex == null)
        {
            string? uri = (string?)image["uri"];
            if (uri != null && uri.StartsWith("data:", StringComparison.Ordinal) && uri.IndexOf(";base64,", StringComparison.Ordinal) > 0)
            {
                return Convert.FromBase64String(uri.Substring(uri.IndexOf(";base64,", StringComparison.Ordinal) + 8));
            }
            throw new GlbException($"image {imageIndex} is not embedded in the file");
        }
        JObject view = (JObject)Array("bufferViews")[viewIndex.Value];
        int offset = (int?)view["byteOffset"] ?? 0;
        int length = (int?)view["byteLength"] ?? 0;
        if (offset < 0 || length < 0 || (long)offset + length > binLength)
        {
            throw new GlbException($"image {imageIndex} reads past the end of the binary chunk");
        }
        byte[] bytes = new byte[length];
        System.Array.Copy(data, binOffset + offset, bytes, 0, length);
        return bytes;
    }

    public static int ComponentSize(int componentType)
    {
        return componentType switch
        {
            5120 or 5121 => 1,
            5122 or 5123 => 2,
            5125 or 5126 => 4,
            _ => throw new GlbException($"component type {componentType} is not a glTF component type")
        };
    }

    public static int ComponentsOf(string? type)
    {
        return type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT2" => 4,
            "MAT3" => 9,
            "MAT4" => 16,
            _ => throw new GlbException($"accessor type '{type}' is not a glTF accessor type")
        };
    }

    private float ReadComponent(int at, int componentType, bool normalized)
    {
        switch (componentType)
        {
            case 5126:
                return BitConverter.ToSingle(data, at);
            case 5121:
                return normalized ? data[at] / 255f : data[at];
            case 5120:
                {
                    sbyte value = unchecked((sbyte)data[at]);
                    return normalized ? Math.Max(value / 127f, -1f) : value;
                }
            case 5123:
                {
                    int value = data[at] | (data[at + 1] << 8);
                    return normalized ? value / 65535f : value;
                }
            case 5122:
                {
                    short value = unchecked((short)(data[at] | (data[at + 1] << 8)));
                    return normalized ? Math.Max(value / 32767f, -1f) : value;
                }
            case 5125:
                return ReadUInt32(data, at);
            default:
                throw new GlbException($"component type {componentType} is not a glTF component type");
        }
    }

    private static float[]? FloatsOf(JToken? token)
    {
        if (token is not JArray array)
        {
            return null;
        }
        float[] values = new float[array.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (float)array[i];
        }
        return values;
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
    }
}
