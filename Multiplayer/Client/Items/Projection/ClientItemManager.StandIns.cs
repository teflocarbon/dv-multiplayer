using Multiplayer.Core.Items.Projection;
using Multiplayer.Client.Items.Inventory;
using Multiplayer.Client.Items.LiveState;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Client.Items.Projection;

internal sealed partial class ClientItemManager
{
    // The local player's inventory as stand-ins (docs/design/items/inventory.md, "Stand-ins"; D14,
    // OI-COLD-09): every row the server keeps in this player's slots is an InventoryStandIn in
    // vanilla's slot, and no object. An update that puts an item in the inventory makes or moves its
    // stand-in (and an object of it here goes); one that takes it anywhere else makes its object
    // from the row, with its state, and the stand-in goes. A put-away by the player
    // (InventoryPutAwayPatch) swaps the object for its stand-in before vanilla adds it, asks the
    // server, and the object goes. The host's own player is the server's (HostInventory); VR belt
    // items (OI-COLD-14) and items with no prefab here stay objects; job papers are stand-ins too,
    // made again from their job (JobPapers, OI-COLD-07).

    /// <summary>Whether this game's own inventory is stand-ins: a remote client's, in a session.</summary>
    internal static bool StandInsActive => NetworkRole.IsRemoteClient && NetworkRole.LocalPlayerId != 0 &&
        DV.InventorySystem.Inventory.Instance != null;

    public bool Active => StandInsActive;

    public bool CatchesDirectAdds => true;

    /// <summary>Whether this object goes into the inventory as a stand-in when the player puts it away.</summary>
    public bool PutsAwayAsStandIn(NetworkedItem item) =>
        StandInsActive && item != null && item.NetId != 0 && item.Item != null &&
        (!shell.DoNotCreateItem(item) || JobPapers.CanBeRow(item.gameObject)) &&
        InventoryStandIn.CanStandIn(item.gameObject) &&
        !DV.InventorySystem.Inventory.Instance.IsDestroyedOnAddedToInventory(item.gameObject);

    /// <summary>
    /// The local player put an object away and vanilla has its stand-in in a slot: the server is
    /// asked for the stash, with the item's state for its row (OI-COLD-13), and the object goes
    /// through the retirement path. A refusal comes back as the item's update and makes it again.
    /// </summary>
    public void CommitPutAway(NetworkedItem item, InventoryStandIn standIn)
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        int slot = inventory.IndexOf(standIn.gameObject);
        ItemInventoryClaimFlags flags = ItemInventoryClaimFlags.None;
        if (standIn.IsEssentialForInventory && slot >= 0)
        {
            if (inventory.GetSlotReservedState(slot)) flags |= ItemInventoryClaimFlags.Reserved;
            if (inventory.GetSlotLockState(slot)) flags |= ItemInventoryClaimFlags.Locked;
        }
        ItemRequest request = new()
        {
            ItemNetId = item.NetId,
            ExpectedRevision = item.ClientPart?.ReserveRevision() ?? item.AuthorityRevision,
            Action = ItemAction.Stash,
            Slot = slot,
            ClaimFlags = flags,
            SavedState = standIn.State
        };
        // The stand-in expects the revision the stash commits at: a move or take-out right after it, or the
        // late echo of a change before it (ProcessReceivedAsClient), is measured against that.
        standIn.Revision = request.ExpectedRevision + 1;
        // Its release and deactivation are the put-away's, not the player's: no drop is reported.
        item.BeginColdStorageRetirement();
        if (item.Item.IsGrabbed())
            item.Item.ForceEndInteraction();
        InventoryRows.Put(item.NetId, standIn.PrefabName, null, standIn.State);
        ItemClient.Current?.SendItemRequest(request);
        NetworkedItemManager.TraceItem("item.put-away-as-stand-in", item, new()
        {
            ["slot"] = slot,
            ["claimFlags"] = flags.ToString(),
            ["stateLength"] = standIn.State.Length
        });
        RetireAmmoOf(item, "put-away-with-its-tool");
        RetireClientProjection(item, "put-away");
    }

    /// <summary>
    /// An update (or a join row) that puts an item in the local player's inventory: its stand-in is
    /// made or moved to its slot, and an object of it here goes. False when it isn't one, or the
    /// item stays an object (it's in the player's hand, or can't be a stand-in).
    /// </summary>
    internal bool ReceiveAsStandIn(ItemUpdate update, string state, bool rollBack = false)
    {
        if (!StandInsActive || !InventoryRows.IsLocalInventory(update))
            return false;
        InventoryStandIn.TryGet(update.ItemNetId, out InventoryStandIn standIn);
        string prefabName = !string.IsNullOrEmpty(update.PrefabName) ? update.PrefabName : standIn?.PrefabName;
        if (!InventoryStandIn.CanStandIn(prefabName))
            return false;
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        // An enabled VR belt slot holds the object itself, as a hand does (OI-COLD-14).
        if (inventory.IsValidVRBeltIndex(update.InventorySlot) && InventoryStandIn.IsBeltSnappablePrefab(prefabName))
            return false;
        NetworkedItem.TryGet(update.ItemNetId, out NetworkedItem item);
        bool held = false, refused = false;
        if (item != null)
        {
            // A loaded magazine stays the object it is, its ammo inside (OI-CON-16).
            if (!InventoryStandIn.CanStandIn(item.gameObject))
                return false;
            held = inventory.GetEquipSlotForItem(item.gameObject) >= 0 || item.Item?.IsGrabbed() == true;
            // The player holds it: their own request decides, unless this is the server refusing
            // it (a take-out rolled back), which puts it back in its slot.
            refused = rollBack || update.MoveCause?.Reason == Core.Items.ChangeCauses.ItemMoveReason.HostCorrection;
            if (held && !refused)
                return false;
        }
        // Its silhouette goes, as the object's did: the item is placed in its slot below.
        if (standIn != null && InventorySilhouettes.IsSilhouette(inventory, standIn))
        {
            VanillaInventorySlots.Forget(inventory, standIn.gameObject);
            standIn.Retire();
            standIn = null;
        }
        if (item != null)
        {
            if (string.IsNullOrEmpty(state))
                state = StandInAmmo.Capture(item.gameObject);
            if (held)
                ReleaseForRollBack(item);
            item.BeginColdStorageRetirement();
            VanillaInventorySlots.Forget(inventory, item.gameObject);
            RetireAmmoOf(item, "its-tool-a-stand-in");
            RetireClientProjection(item, refused ? "take-out-rolled-back" : "now-a-stand-in");
        }
        pendingTakeOuts.Remove(update.ItemNetId);

        standIn ??= InventoryStandIn.For(update.ItemNetId, prefabName);
        standIn.Relation = update.Relation;
        standIn.ClaimSlot = update.InventoryClaimSlot;
        standIn.ClaimFlags = update.InventoryClaimFlags;
        // A correction sets it back, as a refused stash never committed at the revision it expected.
        if (update.AuthorityRevision > standIn.Revision ||
            update.MoveCause?.Reason == Core.Items.ChangeCauses.ItemMoveReason.HostCorrection)
            standIn.Revision = update.AuthorityRevision;
        if (!string.IsNullOrEmpty(state))
            standIn.State = state;
        else if (InventoryRows.TryGet(update.ItemNetId, out InventoryRows.Row row) && !string.IsNullOrEmpty(row.State))
            standIn.State = row.State;
        standIn.Place(update);
        if (update.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create))
            ItemClient.Current?.SendWorldItemProjectionAck(update.ItemNetId, update.AuthorityRevision, ProjectionAck.Projected);
        return true;
    }

    /// <summary>
    /// An update for an item whose stand-in is in the local inventory, taking it anywhere else: the
    /// stand-in goes, and unless the item is destroyed (or made anew by a Create) its object is made
    /// from the row, with its state, for the update to place. The object, or null.
    /// </summary>
    internal NetworkedItem LeaveStandIn(ItemUpdate update)
    {
        // A silhouette isn't stored: its item is elsewhere, and its object, if any, takes the update.
        if (update == null || !InventoryStandIn.TryGet(update.ItemNetId, out InventoryStandIn standIn) ||
            InventorySilhouettes.IsSilhouette(standIn))
            return null;
        VanillaInventorySlots.Forget(DV.InventorySystem.Inventory.Instance, standIn.gameObject);
        NetworkedItem made = null;
        if (update.UpdateType != ItemUpdate.ItemUpdateType.Destroy && !update.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create))
        {
            made = MakeFromRow(new ItemUpdate
            {
                ItemNetId = standIn.NetId,
                PrefabName = standIn.PrefabName,
                AuthorityRevision = standIn.Revision
            }, StandInAmmo.Without(standIn.State));
            if (made != null)
                MakeAmmo(made, standIn.State);
        }
        standIn.Retire();
        InventoryRows.Remove(update.ItemNetId);
        return made;
    }

    // An update a stand-in in the local inventory is already past: this player's own change, echoed
    // after they put the item away again (a take-out and its stash within one round trip), or one
    // that doesn't say where the item is (no ItemState: an object's state change). Either would
    // take the stand-in out of its slot.
    private static bool SupersededForStandIn(ItemUpdate update)
    {
        if (update.UpdateType == ItemUpdate.ItemUpdateType.Destroy ||
            !InventoryStandIn.TryGet(update.ItemNetId, out InventoryStandIn standIn) ||
            InventorySilhouettes.IsSilhouette(standIn))
            return false;
        if (!ItemUpdate.IncludesItemState(update.UpdateType))
            return true;
        return update.MoveCause?.Reason != Core.Items.ChangeCauses.ItemMoveReason.HostCorrection &&
               NetworkRole.LocalPlayerId != 0 && update.OriginatingPlayerId == NetworkRole.LocalPlayerId &&
               update.AuthorityRevision < standIn.Revision;
    }

    // A put-away the server refused (a station fixture, owned-items PR 1c): it keeps the item in
    // the player's hand, but the hand let it go as it went in. It drops at the player's feet, and
    // the drop the game then reports is one the server takes, so both games agree.
    private static void DropRefusedPutAway(NetworkedItem item, ItemUpdate update)
    {
        if (item == null || update.ItemState != ItemState.InHand || update.PlayerId != NetworkRole.LocalPlayerId ||
            item.Item?.IsGrabbed() == true)
            return;
        DV.InventorySystem.Inventory.Instance?.ReturnItemToWorld(item.gameObject);
        item.ClientPart?.QueueObservation("put-away-refused");
    }

    // A loaded tool becoming a stand-in: its ammo objects go with it, their rows nested in the
    // stand-in's state (StandInAmmo, OI-CON-16). Before the tool goes: vanilla empties a destroyed
    // magazine into the world.
    private void RetireAmmoOf(NetworkedItem tool, string reason) =>
        StandInAmmo.RetireLoaded(tool.gameObject, ammo => RetireClientProjection(ammo, reason));

    // A tool made from its stand-in: its ammo is made with it from the nested rows, under the NetIds
    // the server keeps for them, and loaded in its magazine as the server's word, so no load is
    // reported back. The server makes its own copies as it takes the tool out (PlayerItems.Ammo).
    private void MakeAmmo(NetworkedItem tool, string state)
    {
        foreach (StandInAmmo.Entry entry in StandInAmmo.Of(state))
        {
            NetworkedItem ammo = MakeFromRow(new ItemUpdate
            {
                ItemNetId = entry.NetId,
                PrefabName = entry.PrefabName,
                AuthorityRevision = entry.Revision
            }, entry.State?.ToString(Newtonsoft.Json.Formatting.None));
            if (ammo == null)
                continue;
            // One that won't load stays loose, and the tool is still placed: a throw here left it
            // unplaced at the safety position, where its own drop was refused again and again.
            try
            {
                ammo.ReceiveSnapshot(new ItemUpdate
                {
                    UpdateType = ItemUpdate.ItemUpdateType.ItemState,
                    ItemNetId = entry.NetId,
                    PrefabName = entry.PrefabName,
                    AuthorityRevision = entry.Revision,
                    ItemState = ItemState.InMagazine,
                    PlayerHand = PlayerHand.None,
                    MagazineNetId = tool.NetId,
                    MagazineSlot = entry.Slot
                });
            }
            catch (System.Exception exception)
            {
                Multiplayer.LogException($"Inventory: {entry.PrefabName} (netId {entry.NetId}) didn't load into " +
                                         $"{tool.name} (netId {tool.NetId})", exception);
            }
        }
    }

    private static void RetireStandIns()
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        if (inventory != null)
            foreach (InventoryStandIn standIn in InventoryStandIn.All)
                VanillaInventorySlots.Forget(inventory, standIn.gameObject);
        InventoryStandIn.RetireAll();
    }
}
