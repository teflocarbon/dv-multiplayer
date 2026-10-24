using System;
using System.Collections;
using System.Linq;
using DV;
using DV.CabControls;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Server.Items.Inventory;

/// <summary>
/// What players take with them and bring back (docs/design/items/inventory.md, "Cold inventories";
/// D12). A leaving player's hand item goes into a free slot (D5), someone else's items go to their
/// owner's Lost and Found, and the rest of their inventory goes cold: each item's state is kept as
/// a row in <see cref="ColdInventories"/>, in its exact slot, and its object is gone from every
/// game (OI-COLD-02). A joining player's rows are made again in those slots.
/// </summary>
internal static partial class PlayerItems
{
    public static void Clear()
    {
        ColdInventories.Clear();
        ClearDataOnly();
    }

    /// <summary>Host: a player is leaving. Their hand item is stashed, borrowed items go home, and their inventory goes cold.</summary>
    public static void Leave(ServerPlayer player)
    {
        if (player == null || player.Guid == Guid.Empty || NetworkLifecycle.Instance?.IsHost() != true)
            return;

        foreach (LiveItems.LiveItem record in LiveItems.Records.Where(r => r.Holder == player.Guid).ToArray())
        {
            bool inHand = record.Placement == ItemPlacementKind.PlayerHand;
            bool borrowed = IsSomeoneElses(record, player);
            if (!inHand && !borrowed || record.Placement is not (ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory))
                continue;
            // A borrowed item kept as data (OI-COLD-06) is made again first, then goes home.
            if (IsDataOnly(record))
            {
                WithObject(record.NetId, made =>
                {
                    if (made != null && LiveItems.TryGet(made.NetId, out LiveItems.LiveItem now) && now.Holder == player.Guid)
                        SendHome(now, made, player, false, true);
                });
                continue;
            }
            if (!NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null)
                continue;
            // A hand item of their own (or nobody's) goes into a free slot and cold with the rest.
            if (!borrowed && Stash(record, item, player))
                continue;
            SendHome(record, item, player, inHand, borrowed);
        }

        int cold = 0;
        foreach (LiveItems.LiveItem record in LiveItems.Records
                     .Where(r => r.Holder == player.Guid && r.Placement == ItemPlacementKind.PlayerInventory &&
                                 !IsSomeoneElses(r, player)).ToArray())
            if (GoCold(record, player))
                cold++;
        if (cold > 0)
            Multiplayer.Log($"Inventory: {player.Username} left; {cold} item(s) went cold in their slots");
    }

    // Dropped where they stood; someone else's item goes straight to its owner's Lost and Found,
    // online or not, rather than lie there, and so does an owned one that found no free slot.
    private static void SendHome(LiveItems.LiveItem record, NetworkedItem item, ServerPlayer player, bool inHand, bool borrowed)
    {
        Guid owner = record.Owner;
        Drop(record, item, player, inHand ? "was in their hand" + (borrowed ? "" : ", no free slot")
            : "borrowed, owner " + ItemChangeLog.PlayerName(owner));
        if (owner != Guid.Empty)
            LostAndFoundServer.Collect(item, borrowed ? LostItemReason.BorrowerLeft : LostItemReason.InventoryOverflow,
                "left by " + player.Username);
    }

    private static bool IsSomeoneElses(LiveItems.LiveItem record, ServerPlayer player) =>
        record.Owner != Guid.Empty && record.Owner != player.Guid;

    // Into a free slot, its claim first: the leaver's own stash, as the server commits any.
    private static bool Stash(LiveItems.LiveItem record, NetworkedItem item, ServerPlayer player)
    {
        int slot = ServerLedger.FreeSlot(record, player.Guid);
        if (slot < 0)
            return false;
        ItemUpdate snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.FullSync);
        if (snapshot == null)
            return false;
        snapshot.ItemState = ItemState.InInventory;
        snapshot.PlayerId = player.PlayerId;
        snapshot.PlayerHand = PlayerHand.None;
        snapshot.InventorySlot = slot;
        snapshot.InventoryClaimSlot = record.InventoryClaimSlot;
        snapshot.AuthorityRevision = record.Revision;
        if (!LiveItems.TryApplyTransition(item, snapshot, player, ItemTransitionReason.HostLocalState, true,
                out string rejection, cause: ItemChangeLog.Move(ItemMoveReason.StoredInInventory, player,
                    $"was in their hand as they left, slot {slot}")))
        {
            Multiplayer.LogWarning($"Inventory: couldn't stash {record.PrefabName} (netId {record.NetId}) in {player.Username}'s slot {slot} as they left: {rejection}");
            return false;
        }
        item.ApplyServerCanonicalSnapshot(snapshot);
        return true;
    }

    // Dropped where they stood, so the record matches the object lying in the world.
    private static void Drop(LiveItems.LiveItem record, NetworkedItem item, ServerPlayer player, string detail)
    {
        ItemUpdate snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.FullSync);
        if (snapshot == null)
            return;
        snapshot.ItemState = ItemState.Dropped;
        snapshot.PlayerId = 0;
        snapshot.PlayerHand = PlayerHand.None;
        snapshot.ItemPosition = item.transform.position - WorldMover.currentMove;
        snapshot.ItemRotation = item.transform.rotation;
        snapshot.AuthorityRevision = record.Revision;
        if (!LiveItems.TryApplyTransition(item, snapshot, player, ItemTransitionReason.HostLocalState, true,
                out string rejection, cause: ItemChangeLog.Move(ItemMoveReason.DroppedOnDisconnect, player, detail)))
        {
            Multiplayer.LogWarning($"Could not drop item {record.NetId} held by departing player {player.Username}: {rejection}");
            return;
        }
        item.ApplyServerCanonicalSnapshot(snapshot);
        ServerItemManager.Instance?.RegisterHostWorldItem(item);
        NetworkServer.Instance.SendItemUpdate(snapshot, excludePlayer: player);
    }

    /// <summary>
    /// An inventory item goes cold (OI-COLD-02): its row is kept, its record dropped, its object gone
    /// from the server and every game. Its ledger entry stays, home <c>Slot(holder, slot)</c>. A
    /// scene-authored item becomes an ordinary one first, so the scene doesn't make it again.
    /// </summary>
    internal static bool GoCold(LiveItems.LiveItem record, ServerPlayer leaver)
    {
        if (IsDataOnly(record))
        {
            DataGoesCold(record, leaver);
            return true;
        }
        if (record == null || record.Placement != ItemPlacementKind.PlayerInventory)
            return false;
        return Cold(record, record.Holder, record.Slot, leaver,
            $"slot {record.Slot} of {ItemChangeLog.PlayerName(record.Holder)}, kept as data");
    }

    /// <summary>
    /// The one-home repair (D7): an item whose ledger home is an offline holder's slot goes there as
    /// a cold row, from wherever its object is. Not from Lost and Found, whose entry holds it.
    /// </summary>
    internal static bool GoColdInto(LiveItems.LiveItem record, Guid holder, int slot) =>
        record != null && record.Placement != ItemPlacementKind.LostAndFound && !IsDataOnly(record) &&
        PlayerNumbers.Session(holder) == null &&
        Cold(record, holder, slot, null, $"put back in slot {slot} of {ItemChangeLog.PlayerName(holder)}, kept as data");

    private static bool Cold(LiveItems.LiveItem record, Guid holder, int slot, ServerPlayer leaver, string detail)
    {
        if (holder == Guid.Empty || slot < 0 ||
            !NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null || item.Item == null)
            return false;
        if (ItemSaveLoad.IsJobSystemDocument(record.PrefabName))
            return false;
        if (item.IsSceneAuthored)
        {
            ItemSaveLoad.RecordDestroyed(item);
            LiveItems.PromoteAuthoredToPersistent(item);
            item.DetachAuthoredItemKey();
        }
        if (record.PersistentItemId == Guid.Empty)
            return false;

        SavedItemRow row = ItemSaveLoad.RowOf(record, item);
        row.Placement = ItemPlacementKind.PlayerInventory;
        row.PlacementPlayerIdentity = holder;
        row.OwnerIdentity = record.Owner;
        row.Slot = slot;
        if (!ColdInventories.Add(row))
        {
            Multiplayer.LogWarning($"Inventory: {record.PrefabName} ({record.PersistentItemId:D}) is already cold; its object stays");
            return false;
        }

        Retire(item, record, ItemChangeLog.Removal(ItemRemovalReason.WentCold, leaver, detail), leaver);
        return true;
    }

    // As a deposit retires an item into a container: the Destroy goes out now on the reliable channel,
    // then the object goes. OnDestroy logs the removal (the ledger keeps a cold item's entry).
    private static void Retire(NetworkedItem item, LiveItems.LiveItem record, ChangeCause<ItemRemovalReason> cause,
        ServerPlayer leaver)
    {
        ItemUpdate destroy = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Destroy);
        if (destroy != null)
        {
            LiveItems.WriteToSnapshot(record, destroy, ItemTransitionReason.WentCold);
            destroy.RemovalCause = cause;
        }
        item.NoteRemovalCause(cause);
        item.BeginColdStorageRetirement();
        foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
        {
            player.NearbyItems.Remove(item);
            player.KnownItems.Remove(item);
            player.AcknowledgedWorldItems.Remove(item.NetId);
        }
        if (destroy != null)
            NetworkServer.Instance.SendItemUpdate(destroy, excludePlayer: leaver);
        ServerItemManager.Instance?.UnregisterHostWorldItem(item);
        item.Item.ForceEndInteraction();
        InventoryIntegration.RevokeMembership(item.gameObject, item.NetId);
        InventoryIntegration.PurgeContainerMembership(item.gameObject);
        StorageIntegration.MoveTo(item.Item, StorageMembership.None);
        Object.Destroy(item.gameObject);
    }

    /// <summary>
    /// Host: a player joined. The host's own player's cold rows are made again here, in their exact
    /// slots: its game is the server's. A remote player's go live as rows once their game is ready
    /// for items (<see cref="SendRows"/>, OI-COLD-03). The host's own player is admitted before the
    /// load, when it has no rows yet: the load joins it.
    /// </summary>
    public static void Join(ServerPlayer player)
    {
        if (player == null || player.Guid == Guid.Empty || NetworkLifecycle.Instance?.IsHost() != true ||
            player != NetworkServer.Instance.LocalPlayer || !ServerLedger.Loaded)
            return;
        foreach (SavedItemRow row in ColdInventories.Of(player.Guid))
            CoroutineManager.Instance.StartCoroutine(GoLive(player, row));
    }

    // As a cold-container withdrawal makes an item (ContainerServer.Withdraw): made at DV's safety
    // position, its state loaded a frame later, registered in its slot and sent to everyone. The row
    // stays until the item is registered, so nothing else takes its slot meanwhile.
    private static IEnumerator GoLive(ServerPlayer player, SavedItemRow row)
    {
        // The ledger takes it back into its slot, so it waits for the stores' import and the check.
        while (!ServerLedger.Loaded && player.PlayerId != 0)
            yield return null;
        InventoryItemSpec spec = Globals.G?.Items?.items?.FirstOrDefault(candidate => candidate != null &&
            string.Equals(candidate.ItemPrefabName, row.PrefabName, StringComparison.Ordinal));
        if (spec == null)
        {
            Multiplayer.LogWarning($"Inventory: no item called {row.PrefabName}; {player.Username}'s {row.PersistentItemId:D} stays cold");
            yield break;
        }
        GameObject instance = Object.Instantiate(spec.gameObject,
            StartingItemsController.ITEM_INSTANTIATION_SAFETY_POSITION, Quaternion.identity);
        instance.name = row.PrefabName;
        InventoryItemSpec placed = instance.GetComponent<InventoryItemSpec>();
        if (placed != null && row.OwnerIdentity != Guid.Empty)
            placed.BelongsToPlayer = true;
        NetworkedItem item = instance.GetOrAddComponent<NetworkedItem>();
        item.BeginColdMaterialization();

        // DV's own StartingItemsController waits a frame before loading an item's state.
        yield return null;

        try
        {
            if (player.PlayerId == 0)
                throw new InvalidOperationException("they left again");
            if (!ColdInventories.TryGet(row.PersistentItemId, out SavedItemRow still) || still != row)
                throw new InvalidOperationException("its row is gone");
            ItemSaveData saveData = instance.GetComponent<ItemSaveData>();
            if (saveData != null && row.State != null)
            {
                try
                {
                    saveData.LoadItemData(row.State);
                    saveData.PostLoadItemData();
                }
                catch (Exception exception) when (ServerLedger.Ledger.IsLive(row.PersistentItemId))
                {
                    // Its state won't load: it comes back fresh in its owner's Lost and Found (D9, OI-LED-16).
                    item.AbortColdMaterialization();
                    Object.Destroy(instance);
                    ColdInventories.Remove(row.PersistentItemId);
                    CoroutineManager.Instance.StartCoroutine(ItemSaveLoad.RemakeFresh(null, row.PersistentItemId,
                        row.PrefabName, exception.Message));
                    yield break;
                }
            }
            ItemBase itemBase = instance.GetComponent<ItemBase>();
            if (itemBase == null || item.NetId == 0)
                throw new InvalidOperationException("no net id");
            item.Initialize(itemBase, item.NetId, true);
            item.FinaliseTrackedValuesAutomatically();

            // Its own slot if it's still free, else its claim or the first free one. One the ledger
            // sent to Lost and Found (its slot was taken when two identities were merged, OI-ID-19),
            // or with no slot free, goes there by way of the player's feet (OI-START-09's route).
            bool lost = ServerLedger.Ledger.TryGetHome(row.PersistentItemId, out Home home) && home.Kind == HomeKind.LostAndFound;
            int slot = lost ? -1
                : ServerLedger.FreeSlot(row.PersistentItemId, row.OwnerIdentity, row.ClaimSlot, player.Guid, row.Slot, row.ClaimSlot);
            bool overflow = slot < 0;
            if (overflow)
                instance.transform.position = player.AbsoluteWorldPosition + WorldMover.currentMove + Vector3.up * 0.3f;
            ItemUpdate snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Create);
            snapshot.ItemState = overflow ? ItemState.Dropped : ItemState.InInventory;
            snapshot.PlayerId = overflow ? (byte)0 : player.PlayerId;
            if (overflow)
                snapshot.ItemPosition = player.AbsoluteWorldPosition + Vector3.up * 0.3f;
            snapshot.InventorySlot = slot;
            snapshot.InventoryClaimSlot = row.ClaimSlot;
            snapshot.InventoryClaimFlags = row.ClaimFlags;
            ItemIdentity identity = instance.GetComponent<ItemIdentity>() ?? instance.AddComponent<ItemIdentity>();
            identity.Value = row.PersistentItemId;
            ColdInventories.Remove(row.PersistentItemId);
            ChangeCause<ItemMoveReason> cause = ItemChangeLog.Move(ItemMoveReason.WentLive, player,
                overflow ? "its slot was taken" : "slot " + slot);
            LiveItems.LiveItem record = LiveItems.RegisterMaterializedColdItem(item, player, row.OwnerIdentity,
                snapshot, row.PersistentItemId, cause);
            if (record == null)
            {
                ColdInventories.Add(row);
                throw new InvalidOperationException("registration failed");
            }
            if (!overflow && record.Slot != row.Slot && row.Slot >= 0)
                Multiplayer.LogWarning($"Inventory: {player.Username}'s {row.PrefabName} came back in slot {record.Slot}, not its slot {row.Slot}");
            item.ApplyServerCanonicalSnapshot(snapshot);
            ServerItemManager.Instance?.RegisterHostWorldItem(item);
            item.CompleteColdMaterialization();
            ContainerServer.PublishMaterializedItem(snapshot, item);
            // If Lost and Found refuses it (an unowned item), it stays at their feet: in sight either way.
            if (overflow && !LostAndFoundServer.Collect(item, LostItemReason.InventoryOverflow, "its slot was taken"))
                Multiplayer.LogWarning($"Inventory: {player.Username}'s {row.PrefabName} had no slot and Lost and Found refused it; it is at their feet");
        }
        catch (Exception exception)
        {
            item.AbortColdMaterialization();
            Object.Destroy(instance);
            Multiplayer.LogWarning($"Inventory: couldn't make {player.Username}'s {row.PrefabName} ({row.PersistentItemId:D}) again: {exception.Message}; it stays cold");
        }
    }
}
