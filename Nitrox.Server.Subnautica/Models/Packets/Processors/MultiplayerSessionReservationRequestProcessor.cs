using Nitrox.Model.Core;
using Nitrox.Model.MultiplayerSession;
using Nitrox.Model.Subnautica.MultiplayerSession;
using Nitrox.Server.Subnautica.Models.Administration;
using Nitrox.Server.Subnautica.Models.Communication;
using Nitrox.Server.Subnautica.Models.GameLogic;
using Nitrox.Server.Subnautica.Models.Packets.Core;
using Nitrox.Server.Subnautica.Models.Serialization.World;

namespace Nitrox.Server.Subnautica.Models.Packets.Processors;

internal sealed class MultiplayerSessionReservationRequestProcessor(PlayerManager playerManager, SessionManager sessionManager, IKickPlayer playerKicker, ISessionActivity sessionActivity, Func<WorldService> worldServiceProvider, ILogger<MultiplayerSessionReservationRequestProcessor> logger)
    : IAnonPacketProcessor<MultiplayerSessionReservationRequest>
{
    private readonly PlayerManager playerManager = playerManager;
    private readonly SessionManager sessionManager = sessionManager;
    private readonly IKickPlayer playerKicker = playerKicker;
    private readonly ISessionActivity sessionActivity = sessionActivity;
    private readonly Func<WorldService> worldServiceProvider = worldServiceProvider;
    private readonly ILogger<MultiplayerSessionReservationRequestProcessor> logger = logger;

    public async Task Process(AnonProcessorContext context, MultiplayerSessionReservationRequest packet)
    {
        logger.ZLogInformation($"Processing reservation request from {packet.AuthenticationContext.Username}");

        // The network service accepts connections while the world is still loading: a reservation made before the saved players are known reserves a saved player as a new one
        // (the heal rehearsal of 2026-10-26: both players got a new body in the lifepod). The client waits for the answer; the reservation is made when the world is loaded.
        Task worldLoaded = worldServiceProvider().WaitForWorldLoadedAsync(CancellationToken.None);
        if (!worldLoaded.IsCompleted)
        {
            logger.ZLogInformation($"The world is still loading: the reservation of {packet.AuthenticationContext.Username} waits for it");
            await worldLoaded;
        }

        PlayerSettings playerSettings = packet.PlayerSettings;
        AuthenticationContext authenticationContext = packet.AuthenticationContext;

        // A client which crashed leaves its session behind until the connection times out, and with it the player name: let the player come back right away.
        // This runs before the reservation, so it checks the ban list, the password and the name itself first, and it only evicts a session that has been
        // silent for 10 seconds: otherwise anybody behind the same address could kick a player who is still playing, again and again, without the password.
        if (playerManager.TryFindEvictableSession(context.Sender.SessionId, context.Sender.EndPoint, authenticationContext, sessionActivity.GetTimeSinceLastPacket, out SessionId staleSessionId))
        {
            await EvictStaleSessionAsync(authenticationContext.Username, staleSessionId);
        }

        MultiplayerSessionReservation reservation = playerManager.ReservePlayerContext(
            context.Sender.SessionId,
            context.Sender.EndPoint,
            playerSettings,
            authenticationContext);

        logger.ZLogInformation($"Reservation processed successfully: Username: {packet.AuthenticationContext.Username} - {reservation}");
        await context.ReplyAsync(reservation);
    }

    private async Task EvictStaleSessionAsync(string playerName, SessionId staleSessionId)
    {
        logger.ZLogInformation($"{playerName} is connecting again from the same address while session #{staleSessionId} still holds the name, removing the old session");
        await playerKicker.KickPlayer(staleSessionId, "Reconnected from the same address");
        // The disconnect is handled by the network thread, give it a moment before removing the session ourselves
        for (int i = 0; i < 40 && sessionManager.IsConnected(staleSessionId); i++)
        {
            await Task.Delay(50);
        }
        if (sessionManager.IsConnected(staleSessionId))
        {
            await sessionManager.RemoveSessionAsync(staleSessionId);
        }
    }
}
