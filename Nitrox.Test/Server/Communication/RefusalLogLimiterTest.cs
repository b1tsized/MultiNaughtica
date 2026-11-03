using System.Net;

namespace Nitrox.Server.Subnautica.Models.Communication;

/// <summary>
///     A refused connection costs the sender one UDP datagram (with any source address) and cost the server one warning line in a log file that rolls by day with no
///     size limit: a flood filled the disk. Each address is logged at most once a minute, the log at most 20 refusals a minute, and the next line says how many were left out.
/// </summary>
[TestClass]
public class RefusalLogLimiterTest
{
    private static readonly DateTimeOffset t0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void TheFirstRefusalOfAnAddressIsLogged()
    {
        RefusalLogLimiter limiter = new();

        limiter.ShouldLog(Address(1), t0, out int suppressed).Should().BeTrue();

        suppressed.Should().Be(0);
    }

    [TestMethod]
    public void TheSameAddressIsLoggedOncePerMinute()
    {
        RefusalLogLimiter limiter = new();

        limiter.ShouldLog(Address(1), t0, out _).Should().BeTrue();
        limiter.ShouldLog(Address(1), t0.AddSeconds(30), out _).Should().BeFalse();
        limiter.ShouldLog(Address(1), t0.AddSeconds(61), out int suppressed).Should().BeTrue();

        suppressed.Should().Be(1);
    }

    [TestMethod]
    public void AFloodFromManyAddressesIsCappedPerMinute()
    {
        RefusalLogLimiter limiter = new();

        int logged = Enumerable.Range(1, 25).Count(i => limiter.ShouldLog(Address(i), t0.AddMilliseconds(i * 10), out _));

        logged.Should().Be(20);
        limiter.ShouldLog(Address(100), t0.AddSeconds(61), out int suppressed).Should().BeTrue();
        suppressed.Should().Be(5);
    }

    [TestMethod]
    public void MemoryStaysBounded()
    {
        RefusalLogLimiter limiter = new();

        for (int i = 0; i < 10_000; i++)
        {
            limiter.ShouldLog(Address(i), t0.AddSeconds(i), out _);
        }

        limiter.TrackedAddresses.Should().BeLessThanOrEqualTo(1024);
    }

    private static IPAddress Address(int i) => new([10, (byte)(i >> 16), (byte)(i >> 8), (byte)i]);
}
