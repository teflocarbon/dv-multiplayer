namespace Multiplayer.Core.Items;

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
    /// The host's acceptance of our own drop or throw, arriving after we took the item back (a
    /// re-grab, or into the inventory). Applied, it took the item out of the hand again; the
    /// pickup that follows it on the wire is what counts.
    /// </summary>
    public static bool IsSupersededLocalRelease(bool isHost, byte localPlayerId,
        byte originatingPlayerId, WireItemState receivedState, bool locallyPossessed) =>
        !isHost && localPlayerId != 0 && originatingPlayerId == localPlayerId &&
        receivedState is WireItemState.Dropped or WireItemState.Thrown && locallyPossessed;
}
