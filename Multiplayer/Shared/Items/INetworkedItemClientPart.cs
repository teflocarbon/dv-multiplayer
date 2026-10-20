using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Items;

/// <summary>
///     The remote client's half of one <see cref="NetworkedItem"/> (<c>Client/Items/NetworkedItemClient</c>),
///     as the item calls it at the points that code always ran. It exists on a remote client only;
///     <see cref="NetworkedItem.ClientPart"/> is null on the host, where the server's record is the
///     item's truth (its server part is <c>Server/Items/NetworkedItemServer</c>).
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
