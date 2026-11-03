using Nitrox.Model.Core;

namespace Nitrox.Server.Subnautica.Models.Communication;

/// <summary>
///     What the network layer knows about whether a session is still alive.
/// </summary>
internal interface ISessionActivity
{
    /// <summary>
    ///     How long the connection of <paramref name="sessionId" /> has received nothing (a live client sends at least its keep-alive pings), or null when the
    ///     session has no connection any more.
    /// </summary>
    TimeSpan? GetTimeSinceLastPacket(SessionId sessionId);
}
