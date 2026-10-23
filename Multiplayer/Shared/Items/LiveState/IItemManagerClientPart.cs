using System;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Items.LiveState;

/// <summary>
///     The remote client's part of <see cref="NetworkedItemManager"/> (<c>Client/Items/ClientItemManager</c>),
///     as the Shared manager drives it and Shared item code reaches it. It exists on a remote client
///     only; <see cref="NetworkedItemManager.ClientPart"/> is null on the host, which keeps none of
///     this (the server's records are its items).
/// </summary>
public interface IItemManagerClientPart
{
    /// <summary>Every frame: deferred world projections and the unbound items waiting to be classified.</summary>
    void Update();

    /// <summary>Every tick: the client's fall-rescue scan, which reports its items under its terrain.</summary>
    void Tick(uint tick);

    /// <summary>The manager is being destroyed: the session's client item state goes with it.</summary>
    void Clear();

    /// <summary>An item with no host-issued id appeared: classify it (dormant, temporary adoption, or retired).</summary>
    void RegisterUnboundClientItem(NetworkedItem item);

    /// <summary>The host put the item in Lost and Found: its projection here stays hidden.</summary>
    bool IsLostAndFoundTombstoned(ushort itemNetId);

    /// <summary>A job document was made locally: apply the host's snapshot that waited for it.</summary>
    void ApplyPendingSpecialSnapshot(NetworkedItem item);

    /// <summary>A lifecycle-created object takes a host id another projection here holds: retire that one.</summary>
    void ResolveAuthoritativeBindingCollision(NetworkedItem incoming, ushort authoritativeId, Type trackedItemType);

    /// <summary>An authored office prop was taken into a hand or inventory: it lets go of its slot.</summary>
    void DetachAuthoredProjectionForPossession(NetworkedItem item, ItemUpdateData snapshot);
}
