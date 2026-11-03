using System.Collections.Generic;
using Nitrox.Server.Subnautica.Models.Packets.Core;

namespace Nitrox.Server.Subnautica.Models.Packets.Processors;

internal sealed class DefaultServerPacketProcessor(ILogger<DefaultServerPacketProcessor> logger) : IAuthPacketProcessor<Packet>
{
    /// <summary>
    ///     Packet types which don't have a server packet processor but should not be transmitted
    /// </summary>
    private readonly HashSet<Type> defaultPacketProcessorBlacklist =
    [
        typeof(GameModeChanged),
        typeof(DropSimulationOwnership),
        // An answer to a claim for crafting from lockers is the server's alone: relayed, a client could grant a claim to another player that took no items from the lockers
        typeof(CraftingClaimResponse),
        // The refusal of a module install is the server's alone (EntitySpawnedByClientProcessor answers the loser of a race with it): relayed, a client could make every other player's game take a
        // module of its choosing back out of a vehicle slot
        typeof(ModuleSlotRejected),
        // The route and the cooldowns of the engineer console are the server's alone (CyclopsEngineerArbiter): relayed, a client could give any Cyclops the engines route and an overdrive with no seat and no cooldown,
        // or wipe the display of the real engineer
        typeof(CyclopsEngineerState),
        // The state of a drone bay (which drone it holds, whether it is docked) is the server's alone (CyclopsDroneBayProcessor decides it from the requests): relayed, a client could put any Cyclops' drone into its
        // dock or out of it for everybody
        typeof(CyclopsDroneState),
        // Only the server sends these, from admin commands (the debug packets) and from the save loop (the saving notice): relayed, any player could run console
        // commands, spawn, warp or damage in every other player's game of a Debug build, or show everybody a fake "Saving..."
        typeof(DebugConsoleCommand),
        typeof(DebugDamageCreature),
        typeof(DebugSpawnEntity),
        typeof(DebugStartMapPacket),
        typeof(DebugStoryDump),
        typeof(DebugSyncSamplerControl),
        typeof(DebugWarpInto),
        typeof(ServerSaving),
        // The server's own notices: relayed, one packet disconnected every other player with the sender's text, stopped their clients, removed a player who is
        // still there, or showed them admin rights they do not have
        typeof(PlayerKicked),
        typeof(ServerStopped),
        typeof(Disconnect),
        typeof(PermsChanged)
    ];

    private readonly ILogger<DefaultServerPacketProcessor> logger = logger;

    private readonly HashSet<Type> loggingPacketBlackList =
    [
        typeof(AnimationChangeEvent),
        typeof(PlayerMovement),
        typeof(ItemPosition),
        typeof(PlayerStats),
        typeof(StoryGoalExecuted),
        typeof(FMODAssetPacket),
        typeof(FMODCustomEmitterPacket),
        typeof(FMODCustomLoopingEmitterPacket),
        typeof(FMODStudioEmitterPacket),
        typeof(PlayerCinematicControllerCall),
        typeof(TorpedoShot),
        typeof(TorpedoHit),
        typeof(TorpedoTargetAcquired),
        typeof(StasisSphereShot),
        typeof(StasisSphereHit),
        typeof(SeaTreaderChunkPickedUp),
        typeof(ToggleLights)
    ];

    public async Task Process(AuthProcessorContext context, Packet packet)
    {
        Type packetType = packet.GetType();
        if (!loggingPacketBlackList.Contains(packetType))
        {
            logger.ZLogDebug($"Using default packet processor for: {packet} and player #{context.Sender.SessionId}");
        }
        if (defaultPacketProcessorBlacklist.Contains(packetType))
        {
            logger.ZLogErrorOnce($"Player {context.Sender.Name} #{context.Sender.SessionId} sent a packet which is blacklisted by the server. It's likely that the said player is using a modified version of Nitrox and action could be taken accordingly.");
            return;
        }

        await context.SendToOthersAsync(packet);
    }
}
