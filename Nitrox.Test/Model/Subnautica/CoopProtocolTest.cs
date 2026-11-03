using System.Runtime.Serialization;
using BinaryPack.Attributes;
using Nitrox.Model.Core;
using Nitrox.Model.DataStructures;
using Nitrox.Model.Subnautica.DataStructures.GameLogic;
using Nitrox.Model.Subnautica.Packets;

namespace Nitrox.Model.Subnautica;

[TestClass]
public class CoopProtocolTest
{
    private sealed class LayoutExample
    {
        public int Visible { get; set; }
        public string Name { get; } = "";
        public List<int> Numbers { get; set; } = [];
        [IgnoredMember]
        public int NotSent { get; set; }
        [IgnoreDataMember]
        public int AlsoNotSent { get; set; }
        public int[] Array { get; set; } = [];
        private int Hidden { get; set; }
        [SerializableMember]
        private int Secret { get; set; }
    }

    private enum ColourExample
    {
        Red = 1,
        Green = 5
    }

    private sealed class AnotherExample
    {
        public float Value { get; set; }
    }

    [TestMethod]
    public void DescribeType_ListsTheSerializedMembersWithoutAssemblyNames()
    {
        string description = CoopProtocol.DescribeType(typeof(LayoutExample));

        description.Should().Contain("Visible:System.Int32");
        description.Should().Contain("Name:System.String");
        description.Should().Contain("Numbers:System.Collections.Generic.List<System.Int32>");
        description.Should().Contain("Array:System.Int32[]");
        // [IgnoredMember] is what BinaryPack honours; [IgnoreDataMember] only hides a member from the save formats, so it stays in
        description.Should().NotContain(";NotSent:").And.NotContain("{NotSent:").And.NotContain("Hidden");
        description.Should().Contain("AlsoNotSent:System.Int32");
        // a private member that is marked [SerializableMember] is written too
        description.Should().Contain("Secret:System.Int32");
        description.Should().NotContain("mscorlib").And.NotContain("System.Private.CoreLib");
    }

    [TestMethod]
    public void DescribeType_KnowsTheValuesOfAnEnum()
    {
        CoopProtocol.DescribeType(typeof(ColourExample)).Should().Contain("Green=5").And.Contain("Red=1").And.Contain("underlying=System.Int32");
    }

    [TestMethod]
    public void ComputeFingerprint_DoesNotDependOnTheOrderOfTheTypes()
    {
        CoopProtocol.ComputeFingerprint([typeof(LayoutExample), typeof(AnotherExample)])
                    .Should().Be(CoopProtocol.ComputeFingerprint([typeof(AnotherExample), typeof(LayoutExample)]));
    }

    [TestMethod]
    public void ComputeFingerprint_ChangesWithTheSetOfTypes()
    {
        CoopProtocol.ComputeFingerprint([typeof(LayoutExample)])
                    .Should().NotBe(CoopProtocol.ComputeFingerprint([typeof(LayoutExample), typeof(AnotherExample)]));
    }

    [TestMethod]
    public void ComputeFingerprint_IsTwelveHexDigits()
    {
        CoopProtocol.ComputeFingerprint([typeof(LayoutExample)]).Should().MatchRegex("^[0-9a-f]{12}$");
    }

    [TestMethod]
    public void Fingerprint_CoversTheLayoutOfRealPacketsSoAddedFieldsSeparateTheBuilds()
    {
        // B6 added the item and container size to EntityReparented without adding a type: a stock client and this fork could not parse each other's packet
        CoopProtocol.FindSerializedTypes().Should().Contain(typeof(EntityReparented));
        CoopProtocol.DescribeType(typeof(EntityReparented)).Should().Contain("ItemWidth:System.Int32").And.Contain("ContainerHeight:System.Int32");
    }

    [TestMethod]
    public void Fingerprint_CoversTheReplyOfARefusedModuleInstall()
    {
        // U4: a build without ModuleSlotRejected would not understand the reply of a server that sends it, so the two must not connect
        CoopProtocol.FindSerializedTypes().Should().Contain(typeof(ModuleSlotRejected));
        CoopProtocol.DescribeType(typeof(ModuleSlotRejected)).Should().Contain("ItemId:Nitrox.Model.DataStructures.NitroxId").And.Contain("ParentId:Nitrox.Model.DataStructures.NitroxId").And.Contain("Slot:System.String");
    }

    [TestMethod]
    public void FindSerializedTypes_FollowsTheTypesThatPacketsAndEntitiesRefer()
    {
        // NitroxTechType has no attribute of its own and is only the type of Entity.TechType; NitroxId keeps its value in a private [SerializableMember] property
        CoopProtocol.FindSerializedTypes().Should().Contain(typeof(NitroxTechType)).And.Contain(typeof(NitroxId));
        CoopProtocol.DescribeType(typeof(NitroxId)).Should().Contain("guid:System.Guid");
    }

    [TestMethod]
    public void Fingerprint_IsStableAndNotEmpty()
    {
        CoopProtocol.Fingerprint.Should().MatchRegex("^[0-9a-f]{12}$");
        CoopProtocol.Fingerprint.Should().Be(CoopProtocol.ComputeFingerprint(CoopProtocol.FindSerializedTypes()));
    }

    [TestMethod]
    public void ConnectionKey_IsNotTheKeyOfStockNitroxAndNamesVersionAndFingerprint()
    {
        CoopProtocol.ConnectionKey.Should().NotBe(CoopProtocol.STOCK_CONNECTION_KEY);
        CoopProtocol.ConnectionKey.Should().StartWith("nitrox-coop/").And.EndWith($"/{CoopProtocol.Fingerprint}").And.Contain(NitroxEnvironment.CoopVersion);
    }

    [TestMethod]
    public void ExplainRefusal_NamesTheCauseForEachKindOfKey()
    {
        CoopProtocol.ExplainRefusal(CoopProtocol.STOCK_CONNECTION_KEY).Should().Contain("stock Nitrox").And.Contain("install the co-op build");
        CoopProtocol.ExplainRefusal("nitrox-coop/1.9.0.0-coop.0/abcdefabcdef").Should().Contain("different co-op build").And.Contain(CoopProtocol.ConnectionKey);
        CoopProtocol.ExplainRefusal("hello").Should().Contain("not a Nitrox key");
        CoopProtocol.ExplainRefusal("").Should().Contain("no key");
    }

    [TestMethod]
    public void ExplainRefusal_ShowsControlCharactersAsQuestionMarks()
    {
        // The key comes from whoever sends a connection request and ends up in the server log: a line break in it would forge a log line of its own
        string explanation = CoopProtocol.ExplainRefusal("abc\r\n[fake] line");

        explanation.Should().Contain("abc??[fake] line");
        explanation.Should().NotContain("\r").And.NotContain("\n");
    }

    [TestMethod]
    public void DescribeRefusal_TellsAStockServerFromAnotherCoopBuild()
    {
        CoopProtocol.DescribeRefusal(null).Should().Contain("stock Nitrox").And.Contain("same co-op zip");
        CoopProtocol.DescribeRefusal("").Should().Contain("stock Nitrox");
        CoopProtocol.DescribeRefusal("nitrox-coop/1.9.0.0-coop.7/0123456789ab").Should().Contain("co-op 1.9.0.0-coop.7 (protocol 0123456789ab)").And.Contain(CoopProtocol.Describe());
    }
}
