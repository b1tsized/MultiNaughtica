using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Nitrox.Model.Constants;
using Nitrox.Model.Core;
using Nitrox.Model.DataStructures;
using Nitrox.Model.DataStructures.GameLogic;
using Nitrox.Model.DataStructures.Unity;
using Nitrox.Model.GameLogic.PlayerAnimation;
using Nitrox.Model.MultiplayerSession;
using Nitrox.Model.Subnautica.DataStructures.GameLogic;
using Nitrox.Model.Subnautica.MultiplayerSession;
using Nitrox.Server.Subnautica.Models.AppEvents;
using Nitrox.Server.Subnautica.Models.Communication;
using Nitrox.Server.Subnautica.Services;

namespace Nitrox.Server.Subnautica.Models.GameLogic;

// TODO: This manager should only handle player data. Move connection related state to other managers.
internal sealed partial class PlayerManager(SessionManager sessionManager, BanService banService, IOptions<SubnauticaServerOptions> options, ILogger<PlayerManager> logger) : ISessionCleaner
{
    [GeneratedRegex(NitroxConstants.PLAYER_NAME_VALID_REGEX, RegexOptions.NonBacktracking)]
    private static partial Regex PlayerNameRegex();

    private readonly ThreadSafeDictionary<string, Player> allPlayersByName = [];
    private readonly ThreadSafeDictionary<SessionId, Player> connectedPlayersBySessionId = [];
    private readonly ThreadSafeDictionary<SessionId, PlayerContext> reservations = [];
    private readonly ThreadSafeSet<string> reservedPlayerNames = new("Player"); // "Player" is often used to identify the local player and should not be used by any user

    private readonly SessionManager sessionManager = sessionManager;
    private readonly BanService banService = banService;
    private readonly IOptions<SubnauticaServerOptions> options = options;
    private readonly ILogger<PlayerManager> logger = logger;
    private PeerId currentPlayerId;

    /// <summary>All players that have joined since the server started, even if they disconnected</summary>
    public IEnumerable<Player> GetAllPlayers() => allPlayersByName.Values;

    public IEnumerable<Player> ConnectedPlayers()
    {
        return connectedPlayersBySessionId.Values;
    }

    public List<Player> GetConnectedPlayers() => ConnectedPlayers().ToList();

    public List<Player> GetConnectedPlayersExcept(Player excludePlayer)
    {
        return ConnectedPlayers().Where(player => player != excludePlayer).ToList();
    }

    public List<Player> GetConnectedPlayersExcept(SessionId excludeSessionId)
    {
        return ConnectedPlayers().Where(player => player.SessionId != excludeSessionId).ToList();
    }

    public PlayerContext? GetPlayerReservation(SessionId sessionId)
    {
        return reservations.TryGetValue(sessionId, out PlayerContext player) ? player : null;
    }

    public void AddSavedPlayer(Player player)
    {
        allPlayersByName.Add(player.Name, player);
        currentPlayerId = allPlayersByName.Values.Max(x => x.PeerId);
    }

    public MultiplayerSessionReservation ReservePlayerContext(
        SessionId sessionId,
        IPEndPoint endPoint,
        PlayerSettings playerSettings,
        AuthenticationContext authenticationContext)
    {
        if (CheckAdmission(sessionId, endPoint, authenticationContext) is { } rejection)
        {
            return rejection;
        }

        if (sessionManager.GetSessionCount() > options.Value.MaxConnections)
        {
            MultiplayerSessionReservationState rejectedState = MultiplayerSessionReservationState.REJECTED | MultiplayerSessionReservationState.SERVER_PLAYER_CAPACITY_REACHED;
            return new MultiplayerSessionReservation(sessionId, rejectedState);
        }

        string playerName = authenticationContext.Username;

        allPlayersByName.TryGetValue(playerName, out Player? player);
        if (player?.IsPermaDeath == true && options.Value.IsHardcore())
        {
            MultiplayerSessionReservationState rejectedState = MultiplayerSessionReservationState.REJECTED | MultiplayerSessionReservationState.HARDCORE_PLAYER_DEAD;
            return new MultiplayerSessionReservation(sessionId, rejectedState);
        }

        if (reservedPlayerNames.Contains(playerName))
        {
            MultiplayerSessionReservationState rejectedState = MultiplayerSessionReservationState.REJECTED | MultiplayerSessionReservationState.UNIQUE_PLAYER_NAME_CONSTRAINT_VIOLATED;
            return new MultiplayerSessionReservation(sessionId, rejectedState);
        }
        reservedPlayerNames.Add(playerName);

        bool hasSeenPlayerBefore = player != null;
        NitroxId playerNitroxId = hasSeenPlayerBefore ? player.GameObjectId : new NitroxId();
        SubnauticaGameMode gameMode = hasSeenPlayerBefore ? player.GameMode : options.Value.GameMode;
        IntroCinematicMode introCinematicMode = hasSeenPlayerBefore ? IntroCinematicMode.COMPLETED : IntroCinematicMode.LOADING;
        PlayerAnimation animation = new(AnimChangeType.UNDERWATER, AnimChangeState.ON);

        SessionManager.Session session = sessionManager.GetOrCreateSession(endPoint);

        // TODO: At some point, store the muted state of a player
        PlayerContext playerContext = new(playerName, session.Id, playerNitroxId, !hasSeenPlayerBefore, playerSettings, false, gameMode, null, introCinematicMode, animation);

        reservations.Add(session.Id, playerContext);

        return new MultiplayerSessionReservation(sessionId);
    }

    /// <summary>
    ///     How long a session must have sent nothing before a new connection under its name may evict it. A live client sends at least a ping every second, and a
    ///     game that crashed takes far longer than this to be started again; a briefly stalled client stays well below it.
    /// </summary>
    public static readonly TimeSpan STALE_SESSION_SILENCE = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Finds the session that a crashed client left behind under the name of <paramref name="authenticationContext" />, so the player can come back right away
    ///     instead of waiting for the old connection to time out. Only a request that would itself be admitted (not banned, right password, valid name) may evict,
    ///     only a session from the same IP address, and only one that has been silent for <see cref="STALE_SESSION_SILENCE" />: anybody else behind that address
    ///     could otherwise kick a player that is still playing, over and over, without knowing the password.
    /// </summary>
    /// <param name="timeSinceLastPacket">How long a session has sent nothing, or null when it has no connection any more.</param>
    public bool TryFindEvictableSession(SessionId requestingSessionId, IPEndPoint requestingEndPoint, AuthenticationContext authenticationContext, Func<SessionId, TimeSpan?> timeSinceLastPacket,
                                        out SessionId staleSessionId)
    {
        staleSessionId = default;
        if (CheckAdmission(requestingSessionId, requestingEndPoint, authenticationContext) != null ||
            !TryFindStaleSession(authenticationContext.Username, requestingSessionId, requestingEndPoint.Address, out SessionId candidate))
        {
            return false;
        }
        if (timeSinceLastPacket(candidate) is { } silence && silence < STALE_SESSION_SILENCE)
        {
            logger.ZLogWarning($"{authenticationContext.Username} connected again from the same address while session #{candidate} is still active ({silence.TotalSeconds:F1} s since its last packet): it is not evicted");
            return false;
        }
        staleSessionId = candidate;
        return true;
    }

    /// <summary>
    ///     The checks on who is asking: the ban list, the server password and the player name. Null when they pass.
    /// </summary>
    private MultiplayerSessionReservation? CheckAdmission(SessionId sessionId, IPEndPoint endPoint, AuthenticationContext authenticationContext)
    {
        if (banService.IsBanned(endPoint.Address))
        {
            MultiplayerSessionReservationState rejectedState = MultiplayerSessionReservationState.REJECTED | MultiplayerSessionReservationState.PLAYER_BANNED;
            return new MultiplayerSessionReservation(sessionId, rejectedState, banService.GetBanRejectionDetail(endPoint.Address));
        }

        if (!string.IsNullOrEmpty(options.Value.ServerPassword) && (!authenticationContext.ServerPassword.HasValue || authenticationContext.ServerPassword.Value != options.Value.ServerPassword))
        {
            MultiplayerSessionReservationState rejectedState = MultiplayerSessionReservationState.REJECTED | MultiplayerSessionReservationState.AUTHENTICATION_FAILED;
            return new MultiplayerSessionReservation(sessionId, rejectedState);
        }

        if (!PlayerNameRegex().IsMatch(authenticationContext.Username))
        {
            MultiplayerSessionReservationState rejectedState = MultiplayerSessionReservationState.REJECTED | MultiplayerSessionReservationState.INCORRECT_USERNAME;
            return new MultiplayerSessionReservation(sessionId, rejectedState);
        }

        return null;
    }

    /// <summary>
    ///     Finds another session which still holds <paramref name="playerName" /> but was opened from the same IP address as the new
    ///     connection. After a crash the server keeps that session until the connection times out, which would reject the player
    ///     coming back with their own name.
    /// </summary>
    private bool TryFindStaleSession(string playerName, SessionId requestingSessionId, IPAddress requestingAddress, out SessionId staleSessionId)
    {
        staleSessionId = default;
        SessionId? candidate = null;
        foreach (Player player in ConnectedPlayers())
        {
            if (player.Name == playerName && player.SessionId != requestingSessionId)
            {
                candidate = player.SessionId;
            }
        }
        if (candidate == null)
        {
            foreach (PlayerContext reservation in reservations.Values)
            {
                if (reservation.PlayerName == playerName && reservation.SessionId != requestingSessionId)
                {
                    candidate = reservation.SessionId;
                }
            }
        }
        if (candidate == null || sessionManager.GetEndPoint(candidate.Value) is not { } endPoint)
        {
            return false;
        }

        IPAddress staleAddress = endPoint.Address.IsIPv4MappedToIPv6 ? endPoint.Address.MapToIPv4() : endPoint.Address;
        IPAddress newAddress = requestingAddress.IsIPv4MappedToIPv6 ? requestingAddress.MapToIPv4() : requestingAddress;
        if (!staleAddress.Equals(newAddress))
        {
            return false;
        }

        staleSessionId = candidate.Value;
        return true;
    }

    public Player CreatePlayerData(SessionId sessionId, out bool wasBrandNewPlayer)
    {
        PlayerContext playerContext = reservations[sessionId];
        Validate.NotNull(playerContext);

        // The reservation decided "brand new" when the name was reserved. A client that connects while the world is still loading (the saved players are not known yet: a first start with a
        // world repair loads for 15 s, and the Debug instant launch connects the games within seconds) is reserved as a new player although the saved player of that name is there
        // when its turn in the join queue comes. Treated as new, the saved player would get a new body, the lifepod and the starting items, and the next save would keep that.
        bool knownAtJoin = allPlayersByName.TryGetValue(playerContext.PlayerName, out Player player);
        wasBrandNewPlayer = JoinReservationRules.IsBrandNewAtJoin(playerContext.WasBrandNewPlayer, knownAtJoin);
        if (knownAtJoin && playerContext.WasBrandNewPlayer)
        {
            logger.ZLogWarning($"{playerContext.PlayerName} was reserved as a new player while the world was loading, but the saved player of that name is known now: the saved player is used");
            playerContext = new PlayerContext(playerContext.PlayerName, playerContext.SessionId, player.GameObjectId, false, playerContext.PlayerSettings, playerContext.IsMuted, player.GameMode,
                                              playerContext.DrivingVehicle, IntroCinematicMode.COMPLETED, playerContext.Animation);
        }

        if (!knownAtJoin)
        {
            player = new Player(++currentPlayerId,
                                sessionId,
                                playerContext.PlayerName,
                                false,
                                playerContext,
                                NitroxVector3.Zero,
                                NitroxQuaternion.Identity,
                                playerContext.PlayerNitroxId,
                                Optional.Empty,
                                options.Value.DefaultPlayerPerm,
                                new(options.Value.DefaultOxygenValue, options.Value.DefaultMaxOxygenValue, options.Value.DefaultHealthValue, options.Value.DefaultHungerValue, options.Value.DefaultThirstValue, options.Value.DefaultInfectionValue),
                                options.Value.GameMode,
                                [],
                                [],
                                new Dictionary<string, NitroxId>(),
                                new Dictionary<string, float>(),
                                new Dictionary<string, PingInstancePreference>(),
                                [],
                                false,
                                true
            );
            allPlayersByName[playerContext.PlayerName] = player;
        }
        else
        {
            player.SessionId = sessionId;
        }

        connectedPlayersBySessionId.Add(playerContext.SessionId, player);

        // TODO: make a ConnectedPlayer wrapper so this is not stateful
        player.PlayerContext = playerContext;

        // reconnecting players need to have their cell visibility refreshed
        player.ClearVisibleCells();

        reservations.Remove(sessionId);

        return player;
    }

    public bool SetPlayerProperty<T>(SessionId sessionId, T value, Action<Player, T> action)
    {
        if (!TryGetPlayerBySessionId(sessionId, out Player? player))
        {
            return false;
        }

        action(player, value);
        return true;
    }

    public bool TryGetPlayerByName(string playerName, [NotNullWhen(true)] out Player? foundPlayer)
    {
        foundPlayer = null;
        foreach (Player player in ConnectedPlayers())
        {
            if (player.Name == playerName)
            {
                foundPlayer = player;
                return true;
            }
        }

        return false;
    }

    public bool TryGetPlayerBySessionId(SessionId sessionId, [NotNullWhen(true)] out Player? player)
    {
        return connectedPlayersBySessionId.TryGetValue(sessionId, out player);
    }

    public Task OnEventAsync(ISessionCleaner.Args args)
    {
        reservations.Remove(args.Session.Id, out PlayerContext? reservation);
        if (!connectedPlayersBySessionId.Remove(args.Session.Id, out Player player))
        {
            // The session never finished joining: free the name its reservation took
            if (reservation != null)
            {
                reservedPlayerNames.Remove(reservation.PlayerName);
            }
            return Task.CompletedTask;
        }
        reservedPlayerNames.Remove(player.Name);
        // The last mode the client reported tells whether his intro was over: if not, the position the server has is the pose of the cinematic, not a place to stand at (JoiningManager)
        player.IntroUnfinished = player.PlayerContext != null && IntroCinematicRules.IsMidIntro(player.PlayerContext.IntroCinematicMode);
        player.IsOnline = false;
        logger.ZLogInformation($"{player.Name} left the game");
        return Task.CompletedTask;
    }
}
