namespace Multiplayer.Shared.Items.LiveState;

/// <summary>
///     The server's half of one <see cref="NetworkedItem"/> (<c>Server/Items/NetworkedItemServerPart</c>):
///     what the item keeps for the server's own copy. It exists on the server only;
///     <see cref="NetworkedItem.ServerPart"/> is null on a client. Validating players' changes and
///     applying the server's canonical snapshots are <c>NetworkedItemServer</c>'s.
/// </summary>
public interface INetworkedItemServerPart
{
    /// <summary>
    ///     A change in the server's own world (the host's player, or the game) waits for the server item
    ///     manager's dirty pass, which commits it and sends it to the players. Until then the item
    ///     doesn't look at itself again.
    /// </summary>
    bool ObservationPending { get; }

    /// <summary>What last changed the server's own copy (a grab, an inventory call), for an unexplained change's cause.</summary>
    string ObservationTrigger { get; }

    /// <summary>The server's own copy changed, by <paramref name="reason"/>: leave it to the dirty pass.</summary>
    void NoteLocalObservation(string reason);

    /// <summary>The change was taken, suppressed or overtaken by a snapshot: the item may look at itself again.</summary>
    void ClearObservation();

    /// <summary>Whether another player holds the item, by the server's record of its holder.</summary>
    bool HeldByAnotherPlayer { get; }
}
