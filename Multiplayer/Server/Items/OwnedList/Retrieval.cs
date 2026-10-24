using System;
using System.Linq;
using Multiplayer.Core.Diagnostics;
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
using Multiplayer.Server.Items.SaveLoad;
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
/// - In a container: the outermost of their own boxes goes, with everything in it; out of a box
///   that isn't theirs, the item (or their box) comes out alone.
/// - With another player, in their hand or inventory: taken from them, and they're told.
/// - Loaded in a tool: where its tool is. With them, nothing to do; anywhere else it comes out of the
///   tool alone, and a player who has the tool is told.
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
    public const string Busy = "container-busy";

    /// <summary>Whether the owner could retrieve the entry now; why not when they can't.</summary>
    public static bool CanRetrieve(ServerPlayer owner, LedgerEntry entry, out string reason)
    {
        reason = string.Empty;
        if (owner == null || entry == null || !entry.IsLive) { reason = Unknown; return false; }
        if (!entry.Owner.IsPlayer(owner.Guid)) { reason = NotYours; return false; }
        if (ToolOf(entry.Home) is { } tool && WithPlayer(tool, owner.Guid))
        {
            reason = NothingToDo;
            return false;
        }
        switch (RetrievalRule.For(entry.Home, owner.Guid))
        {
            case RetrievalKind.NothingToDo:
                reason = NothingToDo;
                return false;
            case RetrievalKind.NotYet:
                reason = NotYet;
                return false;
            case RetrievalKind.OutermostContainer:
                return Target(owner, entry.Id, out _, out _, out reason);
            default:
                return true;
        }
    }

    /// <summary>
    /// What a retrieval of the item would take, as an object: the item, or the outermost box that
    /// goes with it. Null if none, or if what goes is a row taken out of a box.
    /// </summary>
    public static LiveItems.LiveItem Subject(ServerPlayer owner, Guid itemId) =>
        !ServerLedger.Ledger.TryGetHome(itemId, out Home home) ? null
        : home.Kind != HomeKind.Container ? Find(itemId)
        : Target(owner, itemId, out LiveItems.LiveItem shell, out _, out _) ? shell : null;

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
        LiveItems.LiveItem record;
        if (entry.Home.Kind == HomeKind.Container)
        {
            if (!Target(owner, itemId, out record, out Guid row, out reason))
                return false;
            if (row != Guid.Empty)
                return RetrieveStored(owner, row, out reason);
            // A box mid-deposit or mid-withdrawal waits (OI-LIST-18).
            if (!ContainerServer.Graph.IsSettled(record.PersistentItemId))
            {
                reason = Busy;
                return false;
            }
        }
        else
            record = Find(itemId);
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
        ServerPlayer toolHolder = record.Placement == ItemPlacementKind.Magazine && ToolOf(entry.Home) is { } tool &&
                                  WithPlayer(tool, tool.Holder) ? PlayerNumbers.Session(tool.Holder) : null;
        if (!ServerLedger.SendToLostAndFound(record.PersistentItemId, LostItemReason.RetrievedByOwner,
                "retrieved by " + owner.Username, out reason))
            return false;
        if (toolHolder != null && toolHolder != owner)
            NetworkServer.Instance.SendWhisper($"{owner.Username} took back their {item.Item?.InventorySpecs?.LocalizedName ?? record.PrefabName}", toolHolder);
        return true;
    }

    // What a retrieval of an item in a box takes (OI-LIST-10, OI-LIST-23). Walking up from the item,
    // each box that is the owner's own goes with it; the first box that isn't theirs (someone
    // else's, or nobody's) stays, and what of theirs is in it comes out alone, as a row
    // (<paramref name="row"/>). Reaching the outermost box with every box theirs, that box goes whole
    // (<paramref name="shell"/>). Nothing to do when every box is theirs and the outermost is with
    // them, or in their Lost and Found.
    private static bool Target(ServerPlayer owner, Guid itemId, out LiveItems.LiveItem shell, out Guid row,
        out string reason)
    {
        shell = null;
        row = Guid.Empty;
        reason = string.Empty;
        ContainerGraph graph = ContainerServer.Graph;
        string identity = owner.Guid.ToString("D");
        Guid current = itemId, taking = Guid.Empty;
        for (int depth = 0; depth <= graph.Limits.MaximumDepth + 1; depth++)
        {
            if (!graph.TryGetItem(current, out StoredItemRecord stored))
                break;
            // A box has its shell's item id: stored inside another, the shell is that row; lying in
            // the world or carried, it is a live shell.
            Guid parent = stored.ParentContainerId;
            if (taking == Guid.Empty && graph.OwnerOf(parent) != identity)
                taking = current;
            if (graph.TryGetItem(parent, out _))
            {
                current = parent;
                continue;
            }
            // Out of a box that isn't theirs it comes out alone, wherever that box is: with them (in a
            // box of theirs they hold, they still can't open someone else's), or anywhere else.
            if (taking != Guid.Empty)
            {
                row = taking;
                return true;
            }
            LiveItems.LiveItem outermost = ContainerServer.TryGetShellNetId(parent, out ushort shellNetId) &&
                                           LiveItems.TryGet(shellNetId, out LiveItems.LiveItem live) ? live : null;
            if (outermost != null && (outermost.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory &&
                                      outermost.Holder == owner.Guid ||
                                      outermost.Placement == ItemPlacementKind.LostAndFound))
            {
                reason = NothingToDo;
                return false;
            }
            if (outermost == null)
                break;
            shell = outermost;
            return true;
        }
        reason = Unknown;
        return false;
    }

    // Out of a box that isn't the owner's, alone: made loose and on into their Lost and Found
    // (OI-LIST-23), asked about the item itself.
    private static bool RetrieveStored(ServerPlayer owner, Guid id, out string reason)
    {
        if (!ContainerServer.Graph.TryGetItem(id, out StoredItemRecord stored))
        {
            reason = Unknown;
            return false;
        }
        Guid.TryParse(ContainerServer.Graph.OwnerOf(id), out Guid itemOwner);
        if (!ItemAccessServer.AllowsRow(owner, ItemAccessAction.Retrieve, itemOwner, stored.PrefabName,
                stored.AuthoredItemKey, ItemSaveLoad.FindSpec(stored.PrefabName)?.IsEssential == true, out reason))
            return false;
        NetworkedItem item = ContainerServer.TakeOutLoose(id, ContainerServer.SafetyPosition,
            ItemChangeLog.Move(ItemMoveReason.CollectedToLostAndFound, owner, "retrieved by " + owner.Username), out reason);
        if (item == null)
        {
            if (reason == "container-item-busy")
                reason = Busy;
            return false;
        }
        if (ServerLedger.SendToLostAndFound(id, LostItemReason.RetrievedByOwner, "retrieved by " + owner.Username, out reason))
            return true;
        Multiplayer.LogError($"Retrieval: {stored.PrefabName} ({id:D}) came out of its box but Lost and Found refused it " +
                             $"({reason}); it is loose at the safety position");
        return false;
    }

    // As a departing borrower's items are dropped (PlayerItems.Leave):
    // out of the holder's hand or inventory, where they stand, then on to the owner's Lost and Found.
    private static bool TakeFromHolder(ServerPlayer owner, NetworkedItem item, LiveItems.LiveItem record, out string reason)
    {
        ServerPlayer holder = PlayerNumbers.Session(record.Holder);
        string name = item.Item?.InventorySpecs?.LocalizedName ?? record.PrefabName;
        if (!DropFromHolder(item, record, holder ?? owner, ItemChangeLog.Move(ItemMoveReason.TakenFromPlayer, owner,
                $"{owner.Username} took back their {name}"), out reason))
            return false;
        if (holder != null && holder != owner)
            NetworkServer.Instance.SendWhisper($"{owner.Username} took back their {name}", holder);
        return true;
    }

    /// <summary>Out of its holder's hand or inventory, onto the ground where they stand, sent to everyone.</summary>
    internal static bool DropFromHolder(NetworkedItem item, LiveItems.LiveItem record, ServerPlayer actor,
        ChangeCause<ItemMoveReason> cause, out string reason)
    {
        reason = string.Empty;
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
        if (!LiveItems.TryApplyTransition(item, snapshot, actor,
                ItemTransitionReason.HostLocalState, true, out reason, cause: cause))
            return false;
        item.ApplyServerCanonicalSnapshot(snapshot);
        ServerItemManager.Instance?.RegisterHostWorldItem(item);
        NetworkServer.Instance.SendItemUpdate(snapshot);
        return true;
    }

    // Ammo in a tool's magazine (containers.md, "Ammo in a magazine") is where its tool is: with its
    // owner there's nothing to do; anywhere else it comes out of the tool alone, and a player who
    // has the tool is told.
    private static LiveItems.LiveItem ToolOf(Home home) =>
        home.Kind == HomeKind.Magazine && Guid.TryParse(home.Anchor, out Guid toolId) ? Find(toolId) : null;

    private static bool WithPlayer(LiveItems.LiveItem record, Guid player) =>
        player != Guid.Empty && record.Holder == player &&
        record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory;

    private static LiveItems.LiveItem Find(Guid id) => id == Guid.Empty ? null :
        LiveItems.GetAll().FirstOrDefault(r => r.PersistentItemId == id);
}
