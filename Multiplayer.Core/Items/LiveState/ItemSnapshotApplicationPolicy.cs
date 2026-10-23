using Multiplayer.Core.Items.Projection;

namespace Multiplayer.Core.Items.LiveState;

/// <summary>
/// Separates a canonical acknowledgement from a remote Unity-state application. The player who
/// originated a throw has already executed the base-game physics transition and must not execute
/// the echoed throw a second time.
/// </summary>
public static class ItemSnapshotApplicationPolicy
{
    public static bool IsLocalThrowAcknowledgement(bool isHost, byte localPlayerId,
        byte originatingPlayerId, WireItemState state) =>
        !isHost && localPlayerId != 0 && originatingPlayerId == localPlayerId &&
        state == WireItemState.Thrown;

    public static bool IsLocalVrReleaseAcknowledgement(bool isHost, byte localPlayerId,
        byte originatingPlayerId, WireItemState previousState, WireItemState receivedState,
        byte previousHand) =>
        !isHost && localPlayerId != 0 && originatingPlayerId == localPlayerId &&
        previousState == WireItemState.InHand && receivedState == WireItemState.Dropped &&
        previousHand != 0;

    /// <summary>
    /// The host's echo of our own object-state change: our values, a round trip old. Applied while
    /// the player is still turning a lantern's knob, it set the wick back.
    /// </summary>
    public static bool IsOwnObjectStateEcho(bool isHost, byte localPlayerId, byte originatingPlayerId,
        bool objectStateOnly) =>
        !isHost && localPlayerId != 0 && originatingPlayerId == localPlayerId && objectStateOnly;

    /// <summary>
    /// The host's acceptance of our own drop or throw, arriving after we took the item back (a
    /// re-grab, or into the inventory). Applied, it took the item out of the hand again; the
    /// pickup that follows it on the wire is what counts.
    /// </summary>
    public static bool IsSupersededLocalRelease(bool isHost, byte localPlayerId,
        byte originatingPlayerId, WireItemState receivedState, bool locallyPossessed) =>
        !isHost && localPlayerId != 0 && originatingPlayerId == localPlayerId &&
        receivedState is WireItemState.Dropped or WireItemState.Thrown && locallyPossessed;

    /// <summary>
    /// The host's acceptance of our own hold, arriving after we let go (a VR throw that beat the
    /// round trip; letting go also sends a last in-hand update as the hand clears). Applied, it
    /// made the hold our last sent state, so the drop went out again against an older revision;
    /// the host refused it with a correction to the release pose, and the item stopped dead
    /// mid-flight and fell. The drop we sent since follows it on the wire.
    /// </summary>
    public static bool IsSupersededLocalHold(bool isHost, byte localPlayerId,
        byte originatingPlayerId, WireItemState receivedState, WireItemState localState,
        bool locallyPossessed) =>
        !isHost && localPlayerId != 0 && originatingPlayerId == localPlayerId &&
        receivedState == WireItemState.InHand && !locallyPossessed &&
        localState is WireItemState.Dropped or WireItemState.Thrown;
}
