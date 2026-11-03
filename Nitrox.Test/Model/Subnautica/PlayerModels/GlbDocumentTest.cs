using Newtonsoft.Json.Linq;
using Nitrox.Model.Subnautica.DataStructures.GameLogic.PlayerModels;

namespace Nitrox.Model.Subnautica.PlayerModels;

/// <summary>The reader of GLB files (docs/coop/PLAYER_MODELS.md): the container, the accessors and the images, on a synthetic file; and what it refuses.</summary>
[TestClass]
public class GlbDocumentTest
{
    private const int POSITION = 0, NORMAL = 1, UV = 2, JOINTS = 3, WEIGHTS = 4, INDICES = 5, INVERSE_BIND = 6, TIMES = 7, ROTATIONS = 8, NORMALIZED_WEIGHTS = 9;

    private static GlbDocument Synthetic(Action<JObject>? tweak = null) => GlbDocument.Parse(SyntheticGlb.Build(tweak));

    [TestMethod]
    public void TheValuesOfEveryKindOfAccessorComeBackAsTheyWereWritten()
    {
        GlbDocument document = Synthetic();

        document.ReadFloats(POSITION).Should().Equal(0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f);
        document.ReadFloats(NORMAL).Should().Equal(0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f);
        document.ReadFloats(UV).Should().Equal(0f, 0f, 1f, 0f, 0f, 1f);
        document.ReadJoints(JOINTS).Should().Equal((ushort)0, (ushort)0, (ushort)0, (ushort)0, (ushort)1, (ushort)0, (ushort)0, (ushort)0, (ushort)1, (ushort)0, (ushort)0, (ushort)0);
        document.ReadFloats(WEIGHTS).Should().Equal(1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0.5f, 0.5f, 0f, 0f);
        document.ReadIndices(INDICES).Should().Equal(0, 1, 2);
        document.ReadFloats(INVERSE_BIND).Should().HaveCount(32);
        document.ReadFloats(INVERSE_BIND)[13].Should().Be(-1f);
        document.ReadFloats(INVERSE_BIND)[29].Should().Be(-2f);
        document.ReadFloats(ROTATIONS).Should().HaveCount(8);
    }

    [TestMethod]
    public void ANormalisedUnsignedByteBecomesAFractionOfOne()
    {
        GlbDocument document = Synthetic();

        document.ReadFloats((int)document.Json["normalizedWeightsAccessor"]!).Should().Equal(1f, 0f, 0f, 0f);
    }

    [TestMethod]
    public void AnAccessorKnowsItsTypeItsCountAndItsLimits()
    {
        GlbDocument document = Synthetic();

        GlbAccessor times = document.GetAccessor(TIMES);
        times.ComponentType.Should().Be(5126);
        times.Count.Should().Be(2);
        times.Components.Should().Be(1);
        times.Max.Should().Equal(1f);
        times.Min.Should().Equal(0f);
        GlbAccessor joints = document.GetAccessor(JOINTS);
        joints.ComponentType.Should().Be(5121);
        joints.Components.Should().Be(4);
        document.GetAccessor(INVERSE_BIND).Components.Should().Be(16);
    }

    [TestMethod]
    public void AnEmbeddedImageIsTheBytesOfItsBufferView()
    {
        GlbDocument document = Synthetic();

        document.ReadImage(0).Should().Equal(SyntheticGlb.BaseColorBytes);
        document.ReadImage(1).Should().Equal(SyntheticGlb.NormalBytes);
        document.ReadImage(2).Should().Equal(SyntheticGlb.MetallicRoughnessBytes);
    }

    [TestMethod]
    public void AnImageGivenAsABase64UriIsDecoded()
    {
        GlbDocument document = Synthetic(json => json["images"]![0] = new JObject { ["uri"] = "data:image/png;base64," + Convert.ToBase64String([9, 8, 7]) });

        document.ReadImage(0).Should().Equal(9, 8, 7);
    }

    [TestMethod]
    public void AFileThatIsNotAGlbIsRefusedWithTheReason()
    {
        Action tooShort = () => GlbDocument.Parse(new byte[8]);
        Action noMagic = () => GlbDocument.Parse(new byte[64]);

        tooShort.Should().Throw<GlbException>().WithMessage("*too short*");
        noMagic.Should().Throw<GlbException>().WithMessage("*magic*");
    }

    [TestMethod]
    public void AnOtherVersionAndACutFileAreRefused()
    {
        byte[] version1 = SyntheticGlb.Build();
        version1[4] = 1;
        byte[] whole = SyntheticGlb.Build();
        byte[] cut = whole.Take(whole.Length - 12).ToArray();

        Action wrongVersion = () => GlbDocument.Parse(version1);
        Action truncated = () => GlbDocument.Parse(cut);

        wrongVersion.Should().Throw<GlbException>().WithMessage("*version*");
        truncated.Should().Throw<GlbException>().WithMessage("*cut short*");
    }

    [TestMethod]
    public void AFileThatNeedsAnExtensionIsRefused()
    {
        Action act = () => Synthetic(json => json["extensionsRequired"] = new JArray("KHR_draco_mesh_compression"));

        act.Should().Throw<GlbException>().WithMessage("*KHR_draco_mesh_compression*");
    }

    [TestMethod]
    public void AnExternalBufferAndASparseAccessorAreRefused()
    {
        Action external = () => Synthetic(json => json["buffers"]![0]!["uri"] = "model.bin");
        GlbDocument sparse = Synthetic(json => json["accessors"]![POSITION]!["sparse"] = new JObject());

        external.Should().Throw<GlbException>().WithMessage("*external*");
        Action read = () => sparse.GetAccessor(POSITION);
        read.Should().Throw<GlbException>().WithMessage("*sparse*");
    }

    [TestMethod]
    public void AnAccessorThatReadsPastTheBinaryChunkIsRefusedAndNotRead()
    {
        GlbDocument document = Synthetic(json => json["accessors"]![POSITION]!["count"] = 100000);

        Action act = () => document.ReadFloats(POSITION);

        act.Should().Throw<GlbException>().WithMessage("*past the end*");
    }

    [TestMethod]
    public void AnAccessorThatDoesNotExistIsRefused()
    {
        GlbDocument document = Synthetic();

        Action act = () => document.GetAccessor(99);

        act.Should().Throw<GlbException>().WithMessage("*accessor 99*");
    }

    // A damaged or hostile file (models are downloaded from the internet) must be refused with a GlbException, which every loader catches, and not crash the game
    // with an overflow, an out-of-range read or an allocation of gigabytes

    [TestMethod]
    public void ANegativeAccessorCountIsRefused()
    {
        GlbDocument document = Synthetic(json => json["accessors"]![POSITION]!["count"] = -5);

        Action act = () => document.ReadFloats(POSITION);

        act.Should().Throw<GlbException>();
    }

    [TestMethod]
    public void ANegativeByteStrideIsRefused()
    {
        GlbDocument document = Synthetic(json => json["bufferViews"]![POSITION]!["byteStride"] = -1000);

        Action act = () => document.ReadFloats(POSITION);

        act.Should().Throw<GlbException>();
    }

    [TestMethod]
    public void ANegativeByteOffsetIsRefused()
    {
        GlbDocument document = Synthetic(json => json["accessors"]![POSITION]!["byteOffset"] = -100000);

        Action act = () => document.ReadFloats(POSITION);

        act.Should().Throw<GlbException>();
    }

    [TestMethod]
    public void AnAccessorWithTooManyValuesIsRefused()
    {
        // 16,777,217 one-byte values: inside the binary chunk, but one more than the reader allocates for a single accessor
        const int COUNT = 16_777_217;
        JObject json = new()
        {
            ["asset"] = new JObject { ["version"] = "2.0" },
            ["buffers"] = new JArray(new JObject { ["byteLength"] = COUNT }),
            ["bufferViews"] = new JArray(new JObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = COUNT }),
            ["accessors"] = new JArray(new JObject { ["bufferView"] = 0, ["componentType"] = 5121, ["count"] = COUNT, ["type"] = "SCALAR" })
        };
        GlbDocument document = GlbDocument.Parse(SyntheticGlb.Assemble(System.Text.Encoding.UTF8.GetBytes(json.ToString()), new byte[COUNT]));

        Action act = () => document.GetAccessor(0);

        act.Should().Throw<GlbException>();
    }

    [TestMethod]
    public void ThePaddingOfTheChunksDoesNotConfuseTheReader()
    {
        // a JSON text of every length from 0 to 3 bytes past a multiple of four is padded with blanks
        for (int extra = 0; extra < 4; extra++)
        {
            GlbDocument document = Synthetic(json => json["asset"]!["generator"] = new string('x', 10 + extra));

            document.ReadIndices(INDICES).Should().Equal(0, 1, 2);
        }
    }
}
