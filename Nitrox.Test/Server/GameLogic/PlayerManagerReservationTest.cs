using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nitrox.Model.Configuration;
using Nitrox.Model.Core;
using Nitrox.Model.DataStructures;
using Nitrox.Model.DataStructures.Unity;
using Nitrox.Model.MultiplayerSession;
using Nitrox.Model.Subnautica.MultiplayerSession;
using Nitrox.Server.Subnautica.Models.AppEvents;
using Nitrox.Server.Subnautica.Models.AppEvents.Core;
using Nitrox.Server.Subnautica.Models.Communication;
using Nitrox.Server.Subnautica.Models.Serialization;
using Nitrox.Server.Subnautica.Services;

namespace Nitrox.Server.Subnautica.Models.GameLogic;

/// <summary>
///     A client that crashed leaves its session behind, holding the player's name, until the connection times out. The server evicts that session when the same name
///     connects again from the same address. Anybody else on that address (same home, dorm, VPN exit or carrier NAT) must not be able to use this to kick a player:
///     the request has to pass the ban, password and name checks first, and the old session has to have gone silent.
/// </summary>
[TestClass]
public class PlayerManagerReservationTest
{
    private static readonly IPEndPoint aliceEndPoint = new(IPAddress.Parse("10.0.0.5"), 1000);
    private static readonly IPEndPoint sameAddressEndPoint = new(IPAddress.Parse("10.0.0.5"), 2000);
    private static readonly IPEndPoint otherAddressEndPoint = new(IPAddress.Parse("10.0.0.6"), 2000);

    private BanService banService = null!;
    private SessionManager sessionManager = null!;
    private PlayerManager playerManager = null!;
    private SessionId aliceSession;

    [TestInitialize]
    public async Task Init()
    {
        sessionManager = new SessionManager(new ISessionCleaner.Trigger(() => []), NullLogger<SessionManager>.Instance);
        // A real ban list that bans nobody: its save folder does not exist, so it loads empty and writes nothing
        banService = new BanService(new ServerJsonSerializer(NullLogger<ServerJsonSerializer>.Instance),
                                    Options.Create(new ServerStartOptions { SaveName = $"nitrox-test-{Guid.NewGuid():N}" }),
                                    NullLogger<BanService>.Instance);
        await banService.StartAsync(CancellationToken.None);
        playerManager = new PlayerManager(sessionManager, banService, Options.Create(new SubnauticaServerOptions { ServerPassword = "secret" }), NullLogger<PlayerManager>.Instance);

        aliceSession = sessionManager.GetOrCreateSession(aliceEndPoint).Id;
        playerManager.ReservePlayerContext(aliceSession, aliceEndPoint, Settings(), Auth("Alice", "secret")).ReservationState.Should().Be(MultiplayerSessionReservationState.RESERVED);
    }

    [TestCleanup]
    public async Task Cleanup() => await banService.StopAsync(CancellationToken.None);

    [TestMethod]
    public void AWrongPasswordNeverEvictsTheSessionThatHoldsTheName()
    {
        TryEvict(sameAddressEndPoint, Auth("Alice", "wrong"), TimeSpan.FromSeconds(60), out _).Should().BeFalse();
    }

    [TestMethod]
    public void AMissingPasswordNeverEvictsTheSessionThatHoldsTheName()
    {
        TryEvict(sameAddressEndPoint, new AuthenticationContext("Alice", Optional.Empty), TimeSpan.FromSeconds(60), out _).Should().BeFalse();
    }

    [TestMethod]
    public void AnInvalidNameNeverEvicts()
    {
        TryEvict(sameAddressEndPoint, Auth("x", "secret"), TimeSpan.FromSeconds(60), out _).Should().BeFalse();
    }

    [TestMethod]
    public void ALiveSessionIsNotEvictedEvenWithTheRightPassword()
    {
        TryEvict(sameAddressEndPoint, Auth("Alice", "secret"), TimeSpan.FromSeconds(2), out _).Should().BeFalse();
    }

    [TestMethod]
    public void ASilentSessionFromTheSameAddressIsEvicted()
    {
        TryEvict(sameAddressEndPoint, Auth("Alice", "secret"), TimeSpan.FromSeconds(30), out SessionId stale).Should().BeTrue();

        stale.Should().Be(aliceSession);
    }

    [TestMethod]
    public void ASessionThatHasNoPeerAnyMoreIsEvicted()
    {
        TryEvict(sameAddressEndPoint, Auth("Alice", "secret"), null, out SessionId stale).Should().BeTrue();

        stale.Should().Be(aliceSession);
    }

    [TestMethod]
    public void ASessionFromAnotherAddressIsNeverEvicted()
    {
        TryEvict(otherAddressEndPoint, Auth("Alice", "secret"), TimeSpan.FromSeconds(30), out _).Should().BeFalse();
    }

    [TestMethod]
    public void TheSilenceBoundaryIsTenSeconds()
    {
        TryEvict(sameAddressEndPoint, Auth("Alice", "secret"), TimeSpan.FromSeconds(9.9), out _).Should().BeFalse();
        TryEvict(sameAddressEndPoint, Auth("Alice", "secret"), TimeSpan.FromSeconds(10), out _).Should().BeTrue();
    }

    [TestMethod]
    public void AWrongPasswordIsStillRejectedWhenReserving()
    {
        SessionId session = sessionManager.GetOrCreateSession(otherAddressEndPoint).Id;

        playerManager.ReservePlayerContext(session, otherAddressEndPoint, Settings(), Auth("Bob", "wrong"))
                     .ReservationState.Should().HaveFlag(MultiplayerSessionReservationState.AUTHENTICATION_FAILED);
    }

    private bool TryEvict(IPEndPoint from, AuthenticationContext auth, TimeSpan? aliceSilence, out SessionId stale)
    {
        SessionId requesting = sessionManager.GetOrCreateSession(from).Id;
        return playerManager.TryFindEvictableSession(requesting, from, auth, id => id == aliceSession ? aliceSilence : TimeSpan.Zero, out stale);
    }

    private static AuthenticationContext Auth(string name, string password) => new(name, Optional.Of(password));

    private static PlayerSettings Settings() => new(new NitroxColor(1, 1, 1));
}
