using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Nitrox.Model.Subnautica.DataStructures.GameLogic.PlayerModels;

/// <summary>One node (one bone, one group) of the glTF scene, in glTF space: right-handed, Y up, the character facing +Z.</summary>
public sealed class GlbNode
{
    public GlbNode(string name, int parent, float[] translation, float[] rotation, float[] scale)
    {
        Name = name;
        Parent = parent;
        Translation = translation;
        Rotation = rotation;
        Scale = scale;
    }

    public string Name { get; }

    /// <summary>The index of the parent node, -1 for a root.</summary>
    public int Parent { get; }

    /// <summary>x, y, z.</summary>
    public float[] Translation { get; }

    /// <summary>The quaternion x, y, z, w.</summary>
    public float[] Rotation { get; }

    public float[] Scale { get; }
}

/// <summary>An animation of the file (an emote): its name, how long it runs and how many channels it has. The keyframes are read when the clip is played (<see cref="GlbDocument" />).</summary>
public sealed class GlbClip
{
    public GlbClip(string name, float duration, int channelCount)
    {
        Name = name;
        Duration = duration;
        ChannelCount = channelCount;
    }

    public string Name { get; }

    public float Duration { get; }

    public int ChannelCount { get; }
}

/// <summary>
/// The skinned model of a GLB file for the custom player models (docs/coop/PLAYER_MODELS.md), as plain arrays in glTF space: the skeleton (nodes and the skin's joints with their inverse bind matrices), the mesh
/// (positions, normals, texture coordinates, four joints and four weights per vertex, triangle indices), the material (three image indices and the factors) and the list of clips. One mesh with one primitive is read:
/// that is what the exporter makes of one character. Everything the game needs to build is here; <see cref="GlbSpace" /> turns it into the game's space.
/// </summary>
public sealed class GlbModel
{
    /// <summary>The most nodes a file may have: a Mixamo skeleton with its meshes has well under a hundred.</summary>
    public const int MAX_NODES = 4096;

    private GlbModel(GlbDocument document)
    {
        Document = document;
    }

    public GlbDocument Document { get; }

    public IReadOnlyList<GlbNode> Nodes { get; private set; } = Array.Empty<GlbNode>();

    /// <summary>The nodes at the root of the scene.</summary>
    public IReadOnlyList<int> RootNodes { get; private set; } = Array.Empty<int>();

    /// <summary>The node that carries the mesh.</summary>
    public int MeshNode { get; private set; } = -1;

    /// <summary>The node of every joint of the skin, in the order that JOINTS_0 counts them.</summary>
    public int[] SkinJoints { get; private set; } = Array.Empty<int>();

    /// <summary>16 floats per joint (column-major, glTF space); the identity when the file has none.</summary>
    public float[] InverseBindMatrices { get; private set; } = Array.Empty<float>();

    public float[] Positions { get; private set; } = Array.Empty<float>();

    /// <summary>Empty when the file has no normals.</summary>
    public float[] Normals { get; private set; } = Array.Empty<float>();

    /// <summary>Empty when the file has no texture coordinates; the origin is the top left corner of the image.</summary>
    public float[] Uvs { get; private set; } = Array.Empty<float>();

    /// <summary>Four joint indices (into <see cref="SkinJoints" />) per vertex.</summary>
    public ushort[] Joints { get; private set; } = Array.Empty<ushort>();

    /// <summary>Four weights per vertex.</summary>
    public float[] Weights { get; private set; } = Array.Empty<float>();

    public int[] Indices { get; private set; } = Array.Empty<int>();

    public int VertexCount => Positions.Length / 3;

    public int TriangleCount => Indices.Length / 3;

    /// <summary>The image of the base colour, of the normal map and of the metallic-roughness map; -1 when the material has none.</summary>
    public int BaseColorImage { get; private set; } = -1;

    public int NormalImage { get; private set; } = -1;

    public int MetallicRoughnessImage { get; private set; } = -1;

    public float[] BaseColorFactor { get; private set; } = [1f, 1f, 1f, 1f];

    public float MetallicFactor { get; private set; } = 1f;

    public float RoughnessFactor { get; private set; } = 1f;

    public bool DoubleSided { get; private set; }

    public IReadOnlyList<GlbClip> Clips { get; private set; } = Array.Empty<GlbClip>();

    /// <summary>The lowest and the highest point of the mesh, in glTF space: x, y, z of each.</summary>
    public float[] BoundsMin { get; private set; } = new float[3];

    public float[] BoundsMax { get; private set; } = new float[3];

    public static GlbModel Read(GlbDocument document)
    {
        GlbModel model = new(document);
        model.ReadNodes();
        model.ReadSkinAndMesh();
        model.ReadMaterial();
        model.ReadClips();
        return model;
    }

    /// <summary>The index of the first node with this name, -1 when there is none.</summary>
    public int FindNode(string name)
    {
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (Nodes[i].Name == name)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>The index of the clip with this name in <see cref="Clips" />, -1 when there is none.</summary>
    public int FindClip(string name)
    {
        for (int i = 0; i < Clips.Count; i++)
        {
            if (Clips[i].Name == name)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// All the keys of the clip <paramref name="clipIndex" /> (an index of <see cref="Clips" />): one track for the rotation and one for the translation of every node that it moves; scale and weights are
    /// skipped. Read when an emote is played for the first time (the 134 tracks of one clip of the first model are about 0.7 MB of numbers). Throws <see cref="GlbException" /> for a clip that cannot be read.
    /// </summary>
    public GlbClipData ReadClipData(int clipIndex)
    {
        JArray animations = Document.Array("animations");
        if (clipIndex < 0 || clipIndex >= animations.Count)
        {
            throw new GlbException($"the animation {clipIndex} does not exist");
        }
        JToken animation = animations[clipIndex];
        JArray samplers = animation["samplers"] as JArray ?? new JArray();
        JArray channels = animation["channels"] as JArray ?? new JArray();
        List<GlbTrack> tracks = new(channels.Count);
        float duration = 0f;
        foreach (JToken channel in channels)
        {
            int? samplerIndex = (int?)channel["sampler"];
            int? node = (int?)channel["target"]?["node"];
            string? path = (string?)channel["target"]?["path"];
            if (samplerIndex == null || node == null || path == null || samplerIndex < 0 || samplerIndex >= samplers.Count)
            {
                continue;
            }
            bool rotation = path == "rotation";
            if (!rotation && path != "translation")
            {
                continue;
            }
            if (node < 0 || node >= Nodes.Count)
            {
                continue;
            }
            JToken sampler = samplers[samplerIndex.Value];
            string interpolation = (string?)sampler["interpolation"] ?? "LINEAR";
            if (interpolation == "CUBICSPLINE")
            {
                throw new GlbException($"the animation {clipIndex} uses cubic spline interpolation, which is not supported");
            }
            int? input = (int?)sampler["input"];
            int? output = (int?)sampler["output"];
            if (input == null || output == null)
            {
                continue;
            }
            float[] times = Document.ReadFloats(input.Value);
            float[] values = Document.ReadFloats(output.Value);
            int components = rotation ? 4 : 3;
            if (values.Length < times.Length * components)
            {
                throw new GlbException($"the animation {clipIndex} has {times.Length} keys but only {values.Length} values for the node {node}");
            }
            tracks.Add(new GlbTrack(node.Value, rotation, times, values, interpolation == "STEP"));
            if (times.Length > 0)
            {
                duration = Math.Max(duration, times[times.Length - 1]);
            }
        }
        string name = (string?)animation["name"] ?? $"Animation {clipIndex}";
        return new GlbClipData(name, duration, tracks, Nodes.Count);
    }

    /// <summary>
    ///     The skeleton is walked from child to parent (<see cref="PlayerModelRig.MixamoRest" /> and others): nodes whose parent links loop would recurse until the
    ///     stack overflowed, which closes the game. With one parent per node, a walk up from any node either reaches a root or comes back to a node of the same
    ///     walk; each node is walked once (O(n)).
    /// </summary>
    private static void RefuseCycles(int[] parents)
    {
        // 0 not seen yet, 1 on the walk that is going on, 2 known to lead to a root
        byte[] state = new byte[parents.Length];
        for (int start = 0; start < parents.Length; start++)
        {
            int node = start;
            while (node >= 0 && state[node] == 0)
            {
                state[node] = 1;
                node = parents[node];
            }
            if (node >= 0 && state[node] == 1)
            {
                throw new GlbException($"the parent links of node {node} form a cycle");
            }
            for (node = start; node >= 0 && state[node] == 1; node = parents[node])
            {
                state[node] = 2;
            }
        }
    }

    private void ReadNodes()
    {
        JArray nodes = Document.Array("nodes");
        if (nodes.Count > MAX_NODES)
        {
            throw new GlbException($"the file has {nodes.Count} nodes, more than the {MAX_NODES} the reader accepts");
        }
        int[] parents = Enumerable.Repeat(-1, nodes.Count).ToArray();
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i]["children"] is JArray children)
            {
                foreach (JToken child in children)
                {
                    int index = (int)child;
                    if (index < 0 || index >= nodes.Count)
                    {
                        throw new GlbException($"node {i} has a child {index} that does not exist");
                    }
                    if (index == i)
                    {
                        throw new GlbException($"node {i} is its own child");
                    }
                    if (parents[index] >= 0)
                    {
                        throw new GlbException($"node {index} is the child of two nodes ({parents[index]} and {i})");
                    }
                    parents[index] = i;
                }
            }
        }
        RefuseCycles(parents);
        List<GlbNode> list = new(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            JToken node = nodes[i];
            if (node["matrix"] != null)
            {
                throw new GlbException($"node {i} is given as a matrix, which the reader does not support (the skeleton uses translation and rotation)");
            }
            list.Add(new GlbNode((string?)node["name"] ?? $"node{i}", parents[i], FloatsOf(node["translation"], 3, 0f), FloatsOf(node["rotation"], 4, 0f, 0f, 0f, 1f), FloatsOf(node["scale"], 3, 1f)));
        }
        Nodes = list;

        JArray scenes = Document.Array("scenes");
        int scene = (int?)Document.Json["scene"] ?? 0;
        List<int> roots = [];
        if (scene >= 0 && scene < scenes.Count && scenes[scene]["nodes"] is JArray sceneNodes)
        {
            roots.AddRange(sceneNodes.Select(token => (int)token));
        }
        else
        {
            roots.AddRange(Enumerable.Range(0, list.Count).Where(i => parents[i] < 0));
        }
        RootNodes = roots;
    }

    private void ReadSkinAndMesh()
    {
        JArray nodes = Document.Array("nodes");
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i]["mesh"] != null && nodes[i]["skin"] != null)
            {
                MeshNode = i;
                break;
            }
        }
        if (MeshNode < 0)
        {
            throw new GlbException("the file has no skinned mesh (a node with a mesh and a skin)");
        }

        JToken meshNode = nodes[MeshNode];
        JToken skin = Document.Array("skins")[(int)meshNode["skin"]!];
        SkinJoints = ((JArray)skin["joints"]!).Select(token => (int)token).ToArray();
        int? matrices = (int?)skin["inverseBindMatrices"];
        if (matrices != null)
        {
            InverseBindMatrices = Document.ReadFloats(matrices.Value);
            if (InverseBindMatrices.Length != SkinJoints.Length * 16)
            {
                throw new GlbException($"the skin has {SkinJoints.Length} joints and {InverseBindMatrices.Length / 16} inverse bind matrices");
            }
        }
        else
        {
            InverseBindMatrices = new float[SkinJoints.Length * 16];
            for (int joint = 0; joint < SkinJoints.Length; joint++)
            {
                InverseBindMatrices[joint * 16] = InverseBindMatrices[joint * 16 + 5] = InverseBindMatrices[joint * 16 + 10] = InverseBindMatrices[joint * 16 + 15] = 1f;
            }
        }

        JToken mesh = Document.Array("meshes")[(int)meshNode["mesh"]!];
        JArray primitives = (JArray)mesh["primitives"]!;
        if (primitives.Count == 0)
        {
            throw new GlbException("the mesh has no primitive");
        }
        JToken primitive = primitives[0];
        int mode = (int?)primitive["mode"] ?? 4;
        if (mode != 4)
        {
            throw new GlbException($"the mesh is drawn in mode {mode}; only triangles (4) are supported");
        }
        JToken attributes = primitive["attributes"]!;
        Positions = Document.ReadFloats(Required(attributes, "POSITION"));
        Normals = attributes["NORMAL"] != null ? Document.ReadFloats((int)attributes["NORMAL"]!) : Array.Empty<float>();
        Uvs = attributes["TEXCOORD_0"] != null ? Document.ReadFloats((int)attributes["TEXCOORD_0"]!) : Array.Empty<float>();
        Joints = Document.ReadJoints(Required(attributes, "JOINTS_0"));
        Weights = Document.ReadFloats(Required(attributes, "WEIGHTS_0"));
        int vertices = Positions.Length / 3;
        if (Joints.Length != vertices * 4 || Weights.Length != vertices * 4 || (Normals.Length != 0 && Normals.Length != vertices * 3) || (Uvs.Length != 0 && Uvs.Length != vertices * 2))
        {
            throw new GlbException("the vertex attributes of the mesh do not have the same count");
        }
        if (primitive["indices"] != null)
        {
            Indices = Document.ReadIndices((int)primitive["indices"]!);
        }
        else
        {
            Indices = Enumerable.Range(0, vertices).ToArray();
        }
        if (Indices.Length % 3 != 0 || Indices.Any(index => index < 0 || index >= vertices))
        {
            throw new GlbException("the triangle indices of the mesh are not valid");
        }
        foreach (ushort joint in Joints)
        {
            if (joint >= SkinJoints.Length)
            {
                throw new GlbException($"a vertex uses joint {joint} and the skin has {SkinJoints.Length}");
            }
        }

        BoundsMin = [float.MaxValue, float.MaxValue, float.MaxValue];
        BoundsMax = [float.MinValue, float.MinValue, float.MinValue];
        for (int i = 0; i < vertices; i++)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                float value = Positions[i * 3 + axis];
                BoundsMin[axis] = Math.Min(BoundsMin[axis], value);
                BoundsMax[axis] = Math.Max(BoundsMax[axis], value);
            }
        }
    }

    private void ReadMaterial()
    {
        JToken meshNode = Document.Array("nodes")[MeshNode];
        JToken primitive = Document.Array("meshes")[(int)meshNode["mesh"]!]["primitives"]![0]!;
        int? materialIndex = (int?)primitive["material"];
        if (materialIndex == null)
        {
            return;
        }
        JToken material = Document.Array("materials")[materialIndex.Value];
        DoubleSided = (bool?)material["doubleSided"] ?? false;
        JToken? pbr = material["pbrMetallicRoughness"];
        if (pbr != null)
        {
            BaseColorFactor = pbr["baseColorFactor"] != null ? FloatsOf(pbr["baseColorFactor"], 4, 1f, 1f, 1f, 1f) : BaseColorFactor;
            MetallicFactor = (float?)pbr["metallicFactor"] ?? 1f;
            RoughnessFactor = (float?)pbr["roughnessFactor"] ?? 1f;
            BaseColorImage = ImageOf(pbr["baseColorTexture"]);
            MetallicRoughnessImage = ImageOf(pbr["metallicRoughnessTexture"]);
        }
        NormalImage = ImageOf(material["normalTexture"]);
    }

    private int ImageOf(JToken? textureInfo)
    {
        int? textureIndex = (int?)textureInfo?["index"];
        if (textureIndex == null)
        {
            return -1;
        }
        JArray textures = Document.Array("textures");
        if (textureIndex < 0 || textureIndex >= textures.Count)
        {
            throw new GlbException($"texture {textureIndex} does not exist");
        }
        return (int?)textures[textureIndex.Value]["source"] ?? -1;
    }

    private void ReadClips()
    {
        JArray animations = Document.Array("animations");
        List<GlbClip> clips = new(animations.Count);
        for (int i = 0; i < animations.Count; i++)
        {
            JToken animation = animations[i];
            float duration = 0f;
            if (animation["samplers"] is JArray samplers)
            {
                foreach (JToken sampler in samplers)
                {
                    int? input = (int?)sampler["input"];
                    float[]? max = input != null ? Document.GetAccessor(input.Value).Max : null;
                    if (max is { Length: > 0 })
                    {
                        duration = Math.Max(duration, max[0]);
                    }
                }
            }
            clips.Add(new GlbClip((string?)animation["name"] ?? $"Animation {i}", duration, (animation["channels"] as JArray)?.Count ?? 0));
        }
        Clips = clips;
    }

    private static int Required(JToken attributes, string name)
    {
        int? accessor = (int?)attributes[name];
        return accessor ?? throw new GlbException($"the mesh has no {name}");
    }

    private static float[] FloatsOf(JToken? token, int length, params float[] defaults)
    {
        float[] values = new float[length];
        for (int i = 0; i < length; i++)
        {
            values[i] = defaults.Length == 1 ? defaults[0] : defaults.Length > i ? defaults[i] : 0f;
        }
        if (token is JArray array)
        {
            for (int i = 0; i < length && i < array.Count; i++)
            {
                values[i] = (float)array[i];
            }
        }
        return values;
    }
}
