using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Items.LiveState;

/// <summary>
///     The remote client's half of one <see cref="NetworkedItem"/> (<c>Client/Items/LiveState/NetworkedItemClient</c>),
///     as the item calls it at the points that code always ran. It exists on a remote client only;
///     <see cref="NetworkedItem.ClientPart"/> is null on the host, where the server's record is the
///     item's truth (its server part is <c>Server/Items/LiveState/NetworkedItemServer</c>).
/// </summary>
public interface INetworkedItemClientPart
{
    /// <summary>Whether an item with no host-issued id is waiting for the host to create it.</summary>
    ClientItemUnboundState UnboundState { get; }

    /// <summary>The item started, was enabled or registered: if it has no host id, the manager classifies it.</summary>
    void RegisterIfUnbound();

    /// <summary>
    ///     In the item's LateUpdate: when the host has put it in Lost and Found, keeps its projection
    ///     here hidden, and says so (the item then does nothing else this frame).
    /// </summary>
    bool EnforceLostAndFoundTombstone();

    /// <summary>
    ///     Bound (the host created the item) lets the player use it; unbound gates it, as a
    ///     projection waiting for the host's Create or for its tracked values.
    /// </summary>
    void SetNetworkBinding(bool isBound);

    /// <summary>
    ///     The local player marked the item changed: the item manager looks at it in its next pass
    ///     rather than at the item's LateUpdate.
    /// </summary>
    void QueueObservation(string reason);

    /// <summary>The local player let go of the item (VRTK's Ungrabbed), at <paramref name="releasedAt"/> (realtime).</summary>
    void NoteReleased(float releasedAt);

    /// <summary>The local player took the item (VRTK's Grabbed).</summary>
    void NoteGrabbed();

    /// <summary>
    ///     Holds back a gentle release for <c>ItemReleaseGrace.GraceSeconds</c>: a VR hand swap or a
    ///     re-grab at another spot lets go for a moment, and sent at once that became a drop, with
    ///     the new hold arriving as a change to an item the player no longer had. True while held back.
    /// </summary>
    bool DefersRelease(ItemState currentState);

    /// <summary>
    ///     Whether this game may send the item's object state now (<c>ItemObjectStateAuthority</c>): as
    ///     its holder, or as a player working it where it lies (<paramref name="worldInteraction"/>), or
    ///     turning a book's pages (<paramref name="pageOnly"/>, the pages alone).
    /// </summary>
    bool MaySendObjectState(ItemState currentState, out bool worldInteraction, out bool pageOnly);

    /// <summary>The local player changed the item: ask for adoption, or send the change to the host.</summary>
    void SendLocalObservation(ItemState currentState, ItemState previousObservedState, string reason);

    /// <summary>The revision a change sent now claims, ahead of the host's answer.</summary>
    uint ReserveRevision();

    /// <summary>The host's revision for the item arrived (the item has taken it already).</summary>
    void ObserveCanonicalRevision(ItemUpdateData snapshot);

    /// <summary>A host convergence re-send this copy already agrees with: nothing to apply.</summary>
    bool IgnoresConvergence(ItemUpdateData snapshot);

    /// <summary>The host accepting this game's own drop of the item, which it keeps simulating.</summary>
    bool IsLocalDropAcknowledgement(ItemUpdateData snapshot);
}
