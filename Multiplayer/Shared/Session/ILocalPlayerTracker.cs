using Multiplayer.Shared.Protocol.Packets.Serverbound;

namespace Multiplayer.Shared.Session;

/// <summary>
///     The body of the player at this keyboard, as the server reads it on the host
///     (docs/design/host-local-session.md, "Position read from the tracker"): instead of the host's
///     tracker sending its position through the loopback client a poll or more late, the server
///     samples it right before it handles the host's requests, so every range check sees where the
///     host's player is now. On a client the tracker samples itself each tick and sends the result.
///     Implemented by the client's <c>LocalPlayerTrackerBase</c>; none on a dedicated host.
/// </summary>
public interface ILocalPlayerTracker
{
    /// <summary>
    ///     What changed since the last sample (position, look, posture, car), or null when nothing
    ///     did. <paramref name="reliable"/>: this one shouldn't be lost (a jump, or the player
    ///     stopping), for a sender that has a choice.
    /// </summary>
    ServerboundPlayerPositionPacket Sample(out bool reliable);
}

public static class LocalPlayerTracker
{
    /// <summary>The local player's tracker, or null while there's none (loading, the menu, a dedicated host).</summary>
    public static ILocalPlayerTracker Current { get; private set; }

    public static void Register(ILocalPlayerTracker tracker) => Current = tracker;

    public static void Unregister(ILocalPlayerTracker tracker)
    {
        if (Current == tracker)
            Current = null;
    }
}
