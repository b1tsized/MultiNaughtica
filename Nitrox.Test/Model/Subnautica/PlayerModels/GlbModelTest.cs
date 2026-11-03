using System.Numerics;
using Newtonsoft.Json.Linq;
using Nitrox.Model.Subnautica.DataStructures.GameLogic.PlayerModels;

namespace Nitrox.Model.Subnautica.PlayerModels;

/// <summary>
/// The skinned model of a GLB file (docs/coop/PLAYER_MODELS.md): the skeleton, the mesh, the material and the clips, first on the synthetic file and then on a Mixamo skeleton built from the numbers of <see cref="RigFixtureData" />.
/// </summary>
[TestClass]
public class GlbModelTest
{
    private static GlbModel Synthetic(Action<JObject>? tweak = null) => GlbModel.Read(GlbDocument.Parse(SyntheticGlb.Build(tweak)));

    [TestMethod]
    public void TheSkeletonKeepsItsHierarchyItsNamesAndItsPose()
    {
        GlbModel model = Synthetic();

        model.Nodes.Select(node => node.Name).Should().Equal("root", "boneA", "boneB", "body");
        model.Nodes.Select(node => node.Parent).Should().Equal(-1, 0, 1, 0);
        model.RootNodes.Should().Equal(0);
        model.Nodes[1].Translation.Should().Equal(0f, 1f, 0f);
        model.Nodes[2].Rotation[1].Should().BeApproximately(0.70710678f, 1e-6f);
        model.Nodes[0].Scale.Should().Equal(1f, 1f, 1f);
        model.Nodes[0].Rotation.Should().Equal(0f, 0f, 0f, 1f);
        model.FindNode("boneB").Should().Be(2);
        model.FindNode("nobody").Should().Be(-1);
    }

    [TestMethod]
    public void TheSkinListsItsJointsInTheOrderThatTheVerticesCountThem()
    {
        GlbModel model = Synthetic();

        model.MeshNode.Should().Be(3);
        model.SkinJoints.Should().Equal(1, 2);
        model.InverseBindMatrices.Should().HaveCount(32);
    }

    [TestMethod]
    public void TheMeshHasItsVerticesItsTrianglesAndItsBounds()
    {
        GlbModel model = Synthetic();

        model.VertexCount.Should().Be(3);
        model.TriangleCount.Should().Be(1);
        model.Positions.Should().Equal(0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f);
        model.Normals.Should().HaveCount(9);
        model.Uvs.Should().HaveCount(6);
        model.Joints.Should().HaveCount(12);
        model.Weights.Should().HaveCount(12);
        model.Indices.Should().Equal(0, 1, 2);
        model.BoundsMin.Should().Equal(0f, 0f, 0f);
        model.BoundsMax.Should().Equal(1f, 1f, 0f);
    }

    [TestMethod]
    public void TheMaterialNamesItsImagesAndKeepsItsFactors()
    {
        GlbModel model = Synthetic();

        model.BaseColorImage.Should().Be(0);
        model.NormalImage.Should().Be(1);
        model.MetallicRoughnessImage.Should().Be(2);
        model.BaseColorFactor.Should().Equal(1f, 0.5f, 0.25f, 1f);
        model.MetallicFactor.Should().Be(0.5f);
        model.RoughnessFactor.Should().Be(1f, "the file does not say, and the glTF default is 1");
        model.DoubleSided.Should().BeTrue();
    }

    [TestMethod]
    public void TheClipsAreListedWithTheirLength()
    {
        GlbModel model = Synthetic();

        GlbClip clip = model.Clips.Should().ContainSingle().Subject;
        clip.Name.Should().Be("Wave");
        clip.Duration.Should().Be(1f);
        clip.ChannelCount.Should().Be(1);
    }

    [TestMethod]
    public void AFileWithoutASkinnedMeshIsRefused()
    {
        Action act = () => Synthetic(json => ((JObject)json["nodes"]![3]!).Remove("skin"));

        act.Should().Throw<GlbException>().WithMessage("*no skinned mesh*");
    }

    [TestMethod]
    public void AMeshThatIsNotMadeOfTrianglesIsRefused()
    {
        Action act = () => Synthetic(json => json["meshes"]![0]!["primitives"]![0]!["mode"] = 1);

        act.Should().Throw<GlbException>().WithMessage("*triangles*");
    }

    // The skeleton is walked from child to parent (PlayerModelRig.MixamoRest and others): a node that is its own ancestor recursed until the stack overflowed, which
    // closes the game, so the reader refuses every shape that is not a tree

    [TestMethod]
    public void ANodeThatIsItsOwnChildIsRefused()
    {
        Action act = () => Synthetic(json => json["nodes"]![1]!["children"] = new JArray(1));

        act.Should().Throw<GlbException>();
    }

    [TestMethod]
    public void ANodeWithTwoParentsIsRefused()
    {
        Action act = () => Synthetic(json => json["nodes"]![3]!["children"] = new JArray(2));

        act.Should().Throw<GlbException>();
    }

    [TestMethod]
    public void NodesThatFormACycleAreRefused()
    {
        // boneA -> boneB -> boneA, and nothing else names boneA as a child
        Action act = () => Synthetic(json =>
        {
            json["nodes"]![0]!["children"] = new JArray(3);
            json["nodes"]![2]!["children"] = new JArray(1);
        });

        act.Should().Throw<GlbException>();
    }

    [TestMethod]
    public void TooManyNodesAreRefused()
    {
        Action act = () => Synthetic(json => AddNodes(json, 4097 - 4, chained: false));

        act.Should().Throw<GlbException>();
    }

    [TestMethod]
    public void AChainOfTwoHundredNodesStillLoads()
    {
        GlbModel model = Synthetic(json => AddNodes(json, 200, chained: true));

        model.Nodes.Should().HaveCount(204);
        model.Nodes[203].Parent.Should().Be(202);
        Action rest = () => PlayerModelRig.MixamoRest(model);
        rest.Should().NotThrow();
    }

    /// <summary>Appends <paramref name="count" /> nodes after the four of the synthetic file, the first under the root; each one under the one before when chained.</summary>
    private static void AddNodes(JObject json, int count, bool chained)
    {
        JArray nodes = (JArray)json["nodes"]!;
        int first = nodes.Count;
        ((JArray)nodes[0]!["children"]!).Add(first);
        for (int i = 0; i < count; i++)
        {
            JObject node = new() { ["name"] = $"extra{i}" };
            if (chained && i < count - 1)
            {
                node["children"] = new JArray(first + i + 1);
            }
            nodes.Add(node);
        }
    }

    [TestMethod]
    public void ANodeGivenAsAMatrixIsRefused()
    {
        Action act = () => Synthetic(json => json["nodes"]![1]!["matrix"] = new JArray(new float[16]));

        act.Should().Throw<GlbException>().WithMessage("*matrix*");
    }

    [TestMethod]
    public void ASkinWithTheWrongNumberOfInverseBindMatricesIsRefused()
    {
        Action act = () => Synthetic(json => json["skins"]![0]!["joints"] = new JArray(1, 2, 3));

        act.Should().Throw<GlbException>().WithMessage("*inverse bind matrices*");
    }

    [TestMethod]
    public void TheInverseBindMatricesOfTheReferenceSkeletonUndoTheRestPoseOfItsBones()
    {
        GlbModel model = RigFixture.Reference();
        Matrix4x4[] world = WorldMatrices(model);

        // in the row-vector convention of System.Numerics a joint's inverse bind matrix times its world matrix is the world matrix of the mesh node
        for (int joint = 0; joint < model.SkinJoints.Length; joint++)
        {
            Matrix4x4 inverseBind = FromGltf(model.InverseBindMatrices, joint * 16);
            Matrix4x4 product = inverseBind * world[model.SkinJoints[joint]];

            AssertClose(product, world[model.MeshNode], $"joint {joint} ({model.Nodes[model.SkinJoints[joint]].Name})");
        }
    }

    [TestMethod]
    public void TheRestPoseOfTheReferenceSkeletonIsAnAPoseWithTheHipsAtTheirMeasuredHeight()
    {
        GlbModel model = RigFixture.Reference();
        Matrix4x4[] world = WorldMatrices(model);
        Vector3 Position(string name) => world[model.FindNode(name)].Translation;

        Position("mixamorig:Hips").Y.Should().BeApproximately(0.969f, 0.01f);
        Position("mixamorig:Head").Y.Should().BeApproximately(1.503f, 0.01f);
        // the left side is at +X and the left arm points down and out: hand lower than shoulder, further out than the elbow
        Position("mixamorig:LeftHand").X.Should().BeGreaterThan(Position("mixamorig:LeftForeArm").X);
        Position("mixamorig:LeftHand").Y.Should().BeLessThan(Position("mixamorig:LeftArm").Y);
        Position("mixamorig:RightHand").X.Should().BeLessThan(0f);
    }


    /// <summary>World matrices of every node, in the row-vector convention of System.Numerics (child before parent).</summary>
    private static Matrix4x4[] WorldMatrices(GlbModel model)
    {
        Matrix4x4[] world = new Matrix4x4[model.Nodes.Count];
        bool[] done = new bool[model.Nodes.Count];
        Matrix4x4 Compute(int index)
        {
            if (done[index])
            {
                return world[index];
            }
            GlbNode node = model.Nodes[index];
            Matrix4x4 local = Matrix4x4.CreateScale(node.Scale[0], node.Scale[1], node.Scale[2])
                              * Matrix4x4.CreateFromQuaternion(new Quaternion(node.Rotation[0], node.Rotation[1], node.Rotation[2], node.Rotation[3]))
                              * Matrix4x4.CreateTranslation(node.Translation[0], node.Translation[1], node.Translation[2]);
            world[index] = node.Parent < 0 ? local : local * Compute(node.Parent);
            done[index] = true;
            return world[index];
        }
        for (int i = 0; i < world.Length; i++)
        {
            Compute(i);
        }
        return world;
    }

    /// <summary>A glTF matrix (16 floats, column-major, for column vectors) as the matrix of System.Numerics (for row vectors): the same 16 numbers in the same order.</summary>
    private static Matrix4x4 FromGltf(float[] values, int offset)
    {
        return new Matrix4x4(values[offset], values[offset + 1], values[offset + 2], values[offset + 3], values[offset + 4], values[offset + 5], values[offset + 6], values[offset + 7],
                             values[offset + 8], values[offset + 9], values[offset + 10], values[offset + 11], values[offset + 12], values[offset + 13], values[offset + 14], values[offset + 15]);
    }

    private static void AssertClose(Matrix4x4 actual, Matrix4x4 expected, string what)
    {
        float[] a = [actual.M11, actual.M12, actual.M13, actual.M14, actual.M21, actual.M22, actual.M23, actual.M24, actual.M31, actual.M32, actual.M33, actual.M34, actual.M41, actual.M42, actual.M43, actual.M44];
        float[] e = [expected.M11, expected.M12, expected.M13, expected.M14, expected.M21, expected.M22, expected.M23, expected.M24, expected.M31, expected.M32, expected.M33, expected.M34, expected.M41, expected.M42, expected.M43, expected.M44];
        for (int i = 0; i < 16; i++)
        {
            a[i].Should().BeApproximately(e[i], 2e-3f, $"{what}, element {i}");
        }
    }
}
