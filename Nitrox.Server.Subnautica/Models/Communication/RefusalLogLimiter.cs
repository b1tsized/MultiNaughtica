using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Nitrox.Server.Subnautica.Models.Communication;

/// <summary>
///     Decides which refused connections are written to the log. A connection request costs its sender one UDP datagram, with any source address, and the server
///     log rolls by day without a size limit: logging every refusal let anybody fill the disk. Each address is logged at most once a minute, at most
///     <see cref="MAX_LOGGED_PER_MINUTE" /> refusals are logged in any minute, and the next line that is logged counts the ones left out.
/// </summary>
internal sealed class RefusalLogLimiter
{
    public const int MAX_LOGGED_PER_MINUTE = 20;
    public const int MAX_TRACKED_ADDRESSES = 1024;

    private static readonly TimeSpan window = TimeSpan.FromMinutes(1);

    private readonly Dictionary<IPAddress, DateTimeOffset> lastLoggedByAddress = [];
    private readonly Queue<DateTimeOffset> loggedTimes = [];
    private readonly Lock limiterLock = new();
    private int suppressedSinceLastLog;

    internal int TrackedAddresses
    {
        get
        {
            lock (limiterLock)
            {
                return lastLoggedByAddress.Count;
            }
        }
    }

    /// <param name="suppressed">How many refusals, from any address, were not logged since the last one that was (0 when this one is not logged either).</param>
    public bool ShouldLog(IPAddress address, DateTimeOffset now, out int suppressed)
    {
        suppressed = 0;
        lock (limiterLock)
        {
            while (loggedTimes.Count > 0 && now - loggedTimes.Peek() >= window)
            {
                loggedTimes.Dequeue();
            }
            if ((lastLoggedByAddress.TryGetValue(address, out DateTimeOffset last) && now - last < window) || loggedTimes.Count >= MAX_LOGGED_PER_MINUTE)
            {
                suppressedSinceLastLog++;
                return false;
            }

            if (lastLoggedByAddress.Count >= MAX_TRACKED_ADDRESSES)
            {
                ForgetOldAddresses(now);
            }
            lastLoggedByAddress[address] = now;
            loggedTimes.Enqueue(now);
            suppressed = suppressedSinceLastLog;
            suppressedSinceLastLog = 0;
            return true;
        }
    }

    private void ForgetOldAddresses(DateTimeOffset now)
    {
        foreach (IPAddress old in lastLoggedByAddress.Where(entry => now - entry.Value >= window).Select(entry => entry.Key).ToList())
        {
            lastLoggedByAddress.Remove(old);
        }
        // Cannot happen while at most MAX_LOGGED_PER_MINUTE addresses are logged a minute; a bound that holds whatever the clock does
        if (lastLoggedByAddress.Count >= MAX_TRACKED_ADDRESSES)
        {
            lastLoggedByAddress.Clear();
        }
    }
}
