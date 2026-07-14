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
}
