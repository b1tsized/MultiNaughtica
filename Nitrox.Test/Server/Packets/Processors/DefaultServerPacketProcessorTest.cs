using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nitrox.Model.Core;
using Nitrox.Model.DataStructures;
using Nitrox.Model.DataStructures.GameLogic;
using Nitrox.Model.DataStructures.Unity;
using Nitrox.Model.Packets;
using Nitrox.Model.Subnautica.DataStructures.GameLogic;
using Nitrox.Model.Subnautica.MultiplayerSession;
using Nitrox.Model.Subnautica.Packets;
using Nitrox.Server.Subnautica.Models.AppEvents;
using Nitrox.Server.Subnautica.Models.Communication;
using Nitrox.Server.Subnautica.Models.Packets.Core;
using ZLogger;

namespace Nitrox.Server.Subnautica.Models.Packets.Processors;

/// <summary>
///     The processor that passes on the packets nobody else handles: a packet that only the server may send (an answer to a claim, the refusal of a module install, a game mode change) must never be
///     relayed from a client, or a modified game could talk as the server to everybody else; any other packet still goes to the other players.
/// </summary>
[TestClass]
public class DefaultServerPacketProcessorTest
{
    private RecordingPacketSender packetSender = null!;
    private DefaultServerPacketProcessor relay = null!;
    private ILoggerFactory loggerFactory = null!;
    private Player alice = null!;
    private AuthProcessorContext context = null!;

    [TestInitialize]
    public void Init()
    {
        packetSender = new RecordingPacketSender();
        // the refusal is logged once with ZLogger, which needs a logger that really is one
        loggerFactory = LoggerFactory.Create(builder => builder.AddZLoggerStream(Stream.Null));
        relay = new DefaultServerPacketProcessor(loggerFactory.CreateLogger<DefaultServerPacketProcessor>());
        alice = CreatePlayer(1);
        context = new AuthProcessorContext(alice, packetSender);
    }

    [TestCleanup]
    public void Cleanup() => loggerFactory.Dispose();

    [TestMethod]
    public async Task TheRefusalOfAModuleInstallThatAClientSendsIsNotRelayedToTheOtherPlayers()
    {
        // The refusal is the server's alone (EntitySpawnedByClientProcessor answers with it): a relayed one would make every other client take the named module back out of the named slot
        // of a vehicle, for a player who is not the one whose install lost a race
        await relay.Process(context, new ModuleSlotRejected(new NitroxId(), new NitroxId(), "SeamothModule1"));

        packetSender.SentToOthers.Should().BeEmpty();
    }

    [TestMethod]
    public async Task TheStateOfAnEngineerConsoleThatAClientSendsIsNotRelayedToTheOtherPlayers()
    {
        // The route and the cooldowns are the server's alone: relayed, a client could give any Cyclops the engines route and an overdrive with no seat and no cooldown
        await relay.Process(context, new CyclopsEngineerState(new NitroxId(), (int)EngineerRoute.Engines, 600f, 0f, 0f));

        packetSender.SentToOthers.Should().BeEmpty();
    }

    [TestMethod]
    public async Task TheStateOfADroneBayThatAClientSendsIsNotRelayedToTheOtherPlayers()
    {
        // The bay is the server's alone: relayed, a client could put any Cyclops' drone into its dock or out of it for everybody
        await relay.Process(context, new CyclopsDroneState(new NitroxId(), Optional.Of(new NitroxId()), false));

        packetSender.SentToOthers.Should().BeEmpty();
    }

    [TestMethod]
    public void EveryCyclopsPacketThatNoServerProcessorHandlesIsBlacklistedOrRelayedOnPurpose()
    {
        // A packet that no processor of the server handles is passed on to every other player as it is. For the packets of the Cyclops stations that is right for the two below (a client announces them to the others)
        // and wrong for the ones the server alone authors (a seat, a state, a route): a new packet of a station that is neither handled nor on the blacklist fails here, so that somebody decides
        HashSet<Type> relayedOnPurpose = [typeof(CyclopsDecoyLaunch), typeof(CyclopsFireSuppression)];
        HashSet<Type> handled = typeof(DefaultServerPacketProcessor).Assembly.GetTypes()
                                                                    .Where(type => type is { IsAbstract: false, IsInterface: false })
                                                                    .SelectMany(type => type.GetInterfaces())
                                                                    .Where(face => face.IsGenericType && (face.GetGenericTypeDefinition() == typeof(IAuthPacketProcessor<>) || face.GetGenericTypeDefinition() == typeof(IAnonPacketProcessor<>)))
                                                                    .Select(face => face.GetGenericArguments()[0])
                                                                    .ToHashSet();
        HashSet<Type> blacklist = (HashSet<Type>)typeof(DefaultServerPacketProcessor).GetField("defaultPacketProcessorBlacklist", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(relay)!;

        List<string> undecided = typeof(CyclopsDroneState).Assembly.GetTypes()
                                                           .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(Packet).IsAssignableFrom(type) && type.Name.StartsWith("Cyclops"))
                                                           .Where(type => !handled.Contains(type) && !blacklist.Contains(type) && !relayedOnPurpose.Contains(type))
                                                           .Select(type => type.Name)
                                                           .ToList();

        undecided.Should().BeEmpty("a packet of a Cyclops station that the server alone authors must be on the blacklist of the default processor, or it is relayed from any client to everybody");
        handled.Should().Contain(typeof(CyclopsDroneBay), "the scan sees the processors of the server");
        blacklist.Should().Contain(typeof(CyclopsDroneState));
    }

    [TestMethod]
    public async Task ADebugConsoleCommandThatAClientSendsIsNotRelayed()
    {
        // Only the server sends it (/console, admin): relayed, any player could run console commands in every other player's game of a Debug build
        await relay.Process(context, new DebugConsoleCommand("goal story Goal_Disable_Gun"));

        packetSender.SentToOthers.Should().BeEmpty();
    }

    [TestMethod]
    public void EveryDebugPacketAndTheSavingNoticeAreBlacklisted()
    {
        // The debug packets come from admin commands and the saving notice from the save loop: none has a processor on the server, so each must be on the
        // blacklist, or the default processor passes a client's copy on to everybody. A new debug packet fails here until it is listed.
        HashSet<Type> blacklist = (HashSet<Type>)typeof(DefaultServerPacketProcessor).GetField("defaultPacketProcessorBlacklist", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(relay)!;
        List<Type> serverOnly = typeof(DebugConsoleCommand).Assembly.GetTypes()
                                                           .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(Packet).IsAssignableFrom(type) && type.Name.StartsWith("Debug"))
                                                           .Append(typeof(ServerSaving))
                                                           .ToList();

        serverOnly.Should().HaveCountGreaterThanOrEqualTo(8, "the scan has to find the debug packets for this test to mean anything");
        serverOnly.Where(type => !blacklist.Contains(type)).Select(type => type.Name).Should().BeEmpty();
    }

    [TestMethod]
    public async Task TheAnswerToAClaimThatAClientSendsIsNotRelayedToTheOtherPlayers()
    {
        await relay.Process(context, new CraftingClaimResponse(1, CraftingClaimOutcome.Granted, []));

        packetSender.SentToOthers.Should().BeEmpty();
    }

    [TestMethod]
    public async Task APacketThatIsNotBlacklistedIsStillPassedOnToTheOtherPlayers()
    {
        CyclopsDecoyLaunch packet = new(new NitroxId());

        await relay.Process(context, packet);

        packetSender.SentToOthers.Should().ContainSingle().Which.Packet.Should().BeSameAs(packet);
        packetSender.SentToOthers[0].Excluded.Should().Be(alice.SessionId);
    }

    private static Player CreatePlayer(ushort sessionId)
    {
        return new Player(sessionId, sessionId, $"Player{sessionId}", false, null, NitroxVector3.Zero, NitroxQuaternion.Identity, new NitroxId(), Optional.Empty, Perms.PLAYER,
                          new PlayerStatsData(100, 100, 100, 100, 100, 0), SubnauticaGameMode.SURVIVAL, [], [], new Dictionary<string, NitroxId>(), new Dictionary<string, float>(),
                          new Dictionary<string, PingInstancePreference>(), [], false, true);
    }

    private sealed class RecordingPacketSender : IPacketSender
    {
        public List<(Packet Packet, SessionId Excluded)> SentToOthers { get; } = [];

        public ValueTask SendPacketAsync<T>(T packet, SessionId sessionId) where T : Packet => throw new AssertFailedException("Not expected: a packet that is not relayed goes to nobody");

        public ValueTask SendPacketToAllAsync<T>(T packet) where T : Packet => throw new AssertFailedException("Not expected");

        public ValueTask SendPacketToOthersAsync<T>(T packet, SessionId excludedSessionId) where T : Packet
        {
            SentToOthers.Add((packet, excludedSessionId));
            return ValueTask.CompletedTask;
        }
    }
}
