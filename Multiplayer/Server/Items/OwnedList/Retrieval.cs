using System;
using System.Linq;
using Multiplayer.Core.Items.Access;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Core.Items.OwnedList;
using Multiplayer.Server.Items.Access;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Items.OwnedList;

/// <summary>
/// An owner takes one of their items back from wherever it is, into their Lost and Found
/// (owned-items.md, "Retrieval", PR 6b). The owner asked, so the Lost and Found's nearby-player
/// protection and grace period don't apply.
/// - Lying somewhere (the ground, a train's cab, a shelf): collected now.
/// - In a container: the outermost container that isn't with them goes, with everything in it.
/// - With another player, in their hand or inventory: taken from them, and they're told.
/// - With them already, in Lost and Found, or missing: nothing to do.
/// - Attached, snapped or installed: not yet (detaching first, and gadget removal, PR 8).
/// A recall is one step (docs/design/items/wire.md), so a retrieval never meets one half done.
/// </summary>
public static class Retrieval
{
    public const string NothingToDo = "nothing-to-retrieve";
    public const string NotYet = "not-retrievable-yet";
    public const string Unknown = "unknown-item";
    public const string NotYours = "not-your-item";

    /// <summary>Whether the owner could retrieve the entry now; why not when they can't.</summary>
    public static bool CanRetrieve(ServerPlayer owner, LedgerEntry entry, out string reason)
    {
        reason = string.Empty;
        if (owner == null || entry == null || !entry.IsLive) { reason = Unknown; return false; }
        if (!entry.Owner.IsPlayer(owner.Guid)) { reason = NotYours; return false; }
        switch (RetrievalRule.For(entry.Home, owner.Guid))
        {
            case RetrievalKind.NothingToDo:
                reason = NothingToDo;
                return false;
            case RetrievalKind.NotYet:
                reason = NotYet;
                return false;
            case RetrievalKind.OutermostContainer:
                return Target(owner, entry.Id, out _, out reason) != null;
            default:
                return true;
        }
    }

    /// <summary>What a retrieval of the item would take: the item, or the outermost container that holds it. Null if none.</summary>
    public static LiveItems.LiveItem Subject(ServerPlayer owner, Guid itemId) =>
        !ServerLedger.Ledger.TryGetHome(itemId, out Home home) ? null
        : home.Kind == HomeKind.Container ? Target(owner, itemId, out _, out _) : Find(itemId);

    /// <summary>The owner retrieves the item. False, with the reason, when it doesn't go.</summary>
    public static bool Retrieve(ServerPlayer owner, Guid itemId, out string reason)
    {
        if (!ServerLedger.Ledger.TryGet(itemId, out LedgerEntry entry))
        {
            reason = Unknown;
            return false;
        }
        if (!CanRetrieve(owner, entry, out reason))
            return false;
        LiveItems.LiveItem record = entry.Home.Kind == HomeKind.Container
            ? Target(owner, itemId, out _, out reason)
            : Find(itemId);
        if (record == null || !NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null)
        {
            reason = reason.Length > 0 ? reason : Unknown;
            return false;
        }
        if (!ItemAccessServer.Allows(owner, ItemAccessAction.Retrieve, item, record, out reason))
            return false;

        if (record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory &&
            !TakeFromHolder(owner, item, record, out reason))
            return false;
        return ServerLedger.SendToLostAndFound(record.PersistentItemId, LostItemReason.RetrievedByOwner,
            "retrieved by " + owner.Username, out reason);
    }

    // An item in a container goes as the outermost container that isn't with its owner: walk up
    // the cold graph from the item to the live shell that holds it all.
    private static LiveItems.LiveItem Target(ServerPlayer owner, Guid itemId, out Guid shellId, out string reason)
    {
        reason = string.Empty;
        shellId = Guid.Empty;
        ContainerGraphSnapshot cold = ContainerServer.Graph.ExportSnapshot();
        Guid current = itemId;
        for (int depth = 0; depth < 16; depth++)
        {
            StoredItemRecord stored = cold.Items.FirstOrDefault(i => i.PersistentItemId == current);
            if (stored == null)
                break;
            ContainerRecord container = cold.Containers.FirstOrDefault(c => c.PersistentContainerId == stored.ParentContainerId);
            if (container == null)
                break;
            // A container stored inside another names its shell item (ParentItemId); one lying in the
            // world or carried is a live shell, bound by its container id.
            LiveItems.LiveItem shell = null;
            Guid parent = container.ParentItemId ?? Guid.Empty;
            if (container.ParentItemId == null && ContainerServer.TryGetShellNetId(container.PersistentContainerId, out ushort shellNetId) &&
                LiveItems.TryGet(shellNetId, out LiveItems.LiveItem live))
            {
                shell = live;
                parent = live.PersistentItemId;
            }
            else if (parent != Guid.Empty)
                shell = Find(parent);
            else
                break;
            if (shell != null)
            {
                bool withOwner = shell.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory &&
                                 shell.Holder == owner.Guid;
                if (withOwner)
                {
                    reason = NothingToDo;
                    return null;
                }
                shellId = parent;
                return shell;
            }
            current = parent;
        }
        reason = Unknown;
        return null;
    }

    // As a departing borrower's items are dropped (PlayerItems.Leave):
    // out of the holder's hand or inventory, where they stand, then on to the owner's Lost and Found.
    private static bool TakeFromHolder(ServerPlayer owner, NetworkedItem item, LiveItems.LiveItem record, out string reason)
    {
        reason = string.Empty;
        ServerPlayer holder = PlayerNumbers.Session(record.Holder);
        ItemUpdate snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.FullSync);
        if (snapshot == null)
        {
            reason = Unknown;
            return false;
        }
        snapshot.ItemState = ItemState.Dropped;
        snapshot.PlayerId = 0;
        snapshot.PlayerHand = PlayerHand.None;
        snapshot.ItemPosition = item.transform.position - WorldMover.currentMove;
        snapshot.ItemRotation = item.transform.rotation;
        snapshot.AuthorityRevision = record.Revision;
        string name = item.Item?.InventorySpecs?.LocalizedName ?? record.PrefabName;
        if (!LiveItems.TryApplyTransition(item, snapshot, holder ?? owner,
                ItemTransitionReason.HostLocalState, true, out reason,
                cause: ItemChangeLog.Move(ItemMoveReason.TakenFromPlayer, owner,
                    $"{owner.Username} took back their {name}")))
            return false;
        item.ApplyServerCanonicalSnapshot(snapshot);
        ServerItemManager.Instance?.RegisterHostWorldItem(item);
        NetworkServer.Instance.SendItemUpdate(snapshot);
        if (holder != null && holder != owner)
            NetworkServer.Instance.SendWhisper($"{owner.Username} took back their {name}", holder);
        return true;
    }

    private static LiveItems.LiveItem Find(Guid id) => id == Guid.Empty ? null :
        LiveItems.GetAll().FirstOrDefault(r => r.PersistentItemId == id);
}
