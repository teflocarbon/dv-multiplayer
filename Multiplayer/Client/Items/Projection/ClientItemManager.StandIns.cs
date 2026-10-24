using Multiplayer.Client.Items.Inventory;
using Multiplayer.Client.Items.LiveState;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
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
    // server, and the object goes. The host's own player keeps objects until C4; VR belt items
    // (OI-COLD-14), job papers (C7) and items with no prefab here stay objects.

    /// <summary>Whether this game's own inventory is stand-ins: a remote client's, in a session.</summary>
    internal static bool StandInsActive => NetworkRole.IsRemoteClient && NetworkRole.LocalPlayerId != 0 &&
        DV.InventorySystem.Inventory.Instance != null;

    /// <summary>Whether an item of this prefab can be a stand-in: a prefab known here, not job papers.</summary>
    internal static bool CanStandIn(string prefabName) =>
        InventoryStandIn.FindSpec(prefabName) != null && !NetworkedItemManager.IsJobDocumentPrefab(prefabName);

    /// <summary>Whether this object goes into the inventory as a stand-in when the player puts it away.</summary>
    internal bool PutsAwayAsStandIn(NetworkedItem item) =>
        StandInsActive && item != null && item.NetId != 0 && item.Item != null && !shell.DoNotCreateItem(item) &&
        CanStandIn(item.Item.InventorySpecs?.itemPrefabName) &&
        !DV.InventorySystem.Inventory.Instance.IsDestroyedOnAddedToInventory(item.gameObject);

    /// <summary>The stand-in an object of the local player's becomes as it's put away, with its row's fields and state.</summary>
    internal static InventoryStandIn StandInFor(NetworkedItem item)
    {
        InventoryStandIn standIn = InventoryStandIn.For(item.NetId, item.Item.InventorySpecs.itemPrefabName);
        standIn.Relation = item.Relation;
        standIn.ClaimSlot = item.InventoryClaimSlot;
        standIn.ClaimFlags = item.InventoryClaimFlags;
        standIn.Revision = item.AuthorityRevision;
        standIn.State = ItemStateText.Capture(item.gameObject);
        return standIn;
    }

    /// <summary>
    /// The local player put an object away and vanilla has its stand-in in a slot: the server is
    /// asked for the stash, with the item's state for its row (OI-COLD-13), and the object goes
    /// through the retirement path. A refusal comes back as the item's update and makes it again.
    /// </summary>
    internal void CommitPutAway(NetworkedItem item, InventoryStandIn standIn)
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
        if (!CanStandIn(prefabName))
            return false;
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        // An enabled VR belt slot holds the object itself, as a hand does (OI-COLD-14).
        if (inventory.IsValidVRBeltIndex(update.InventorySlot) && InventoryStandIn.IsBeltSnappablePrefab(prefabName))
            return false;
        if (NetworkedItem.TryGet(update.ItemNetId, out NetworkedItem item) && item != null)
        {
            bool held = inventory.GetEquipSlotForItem(item.gameObject) >= 0 || item.Item?.IsGrabbed() == true;
            // The player holds it: their own request decides, unless this is the server refusing
            // it (a take-out rolled back), which puts it back in its slot.
            bool refused = rollBack || update.MoveCause?.Reason == Core.Items.ChangeCauses.ItemMoveReason.HostCorrection;
            if (held && !refused)
                return false;
            if (string.IsNullOrEmpty(state))
                state = ItemStateText.Capture(item.gameObject);
            if (held)
                ReleaseForRollBack(item);
            item.BeginColdStorageRetirement();
            VanillaInventorySlots.Forget(inventory, item.gameObject);
            RetireClientProjection(item, refused ? "take-out-rolled-back" : "now-a-stand-in");
        }
        pendingTakeOuts.Remove(update.ItemNetId);

        standIn ??= InventoryStandIn.For(update.ItemNetId, prefabName);
        standIn.Relation = update.Relation;
        standIn.ClaimSlot = update.InventoryClaimSlot;
        standIn.ClaimFlags = update.InventoryClaimFlags;
        if (update.AuthorityRevision > standIn.Revision)
            standIn.Revision = update.AuthorityRevision;
        if (!string.IsNullOrEmpty(state))
            standIn.State = state;
        else if (InventoryRows.TryGet(update.ItemNetId, out InventoryRows.Row row) && !string.IsNullOrEmpty(row.State))
            standIn.State = row.State;
        PlaceStandIn(standIn, update);
        if (update.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create))
            ItemClient.Current?.SendWorldItemProjectionAck(update.ItemNetId, update.AuthorityRevision, true);
        return true;
    }

    // In the slot the server has it in (OI-SLOT-01): an essential in its claim slot through the
    // claim, anything else exactly, as an object was placed before D14.
    private static void PlaceStandIn(InventoryStandIn standIn, ItemUpdate update)
    {
        GameObject gameObject = standIn.gameObject;
        bool reserved = NetworkedItem.HasRetrievalFlag(update.InventoryClaimFlags);
        bool inClaim = update.InventoryClaimSlot >= 0 &&
            (update.InventorySlot < 0 || update.InventorySlot == update.InventoryClaimSlot);
        bool placed = inClaim
            ? InventoryIntegration.RestoreClaim(gameObject, update.InventoryClaimSlot).Succeeded || reserved
            : update.InventorySlot >= 0 && InventoryIntegration.PlaceAt(gameObject, update.InventorySlot) >= 0;
        if (!placed && InventoryIntegration.AddActive(gameObject) < 0)
            Multiplayer.LogWarning($"Inventory: no slot for {standIn.PrefabName} (netId {standIn.NetId}), which the server has in slot {update.InventorySlot}");
    }

    /// <summary>
    /// An update for an item whose stand-in is in the local inventory, taking it anywhere else: the
    /// stand-in goes, and unless the item is destroyed (or made anew by a Create) its object is made
    /// from the row, with its state, for the update to place. The object, or null.
    /// </summary>
    internal NetworkedItem LeaveStandIn(ItemUpdate update)
    {
        if (update == null || !InventoryStandIn.TryGet(update.ItemNetId, out InventoryStandIn standIn))
            return null;
        VanillaInventorySlots.Forget(DV.InventorySystem.Inventory.Instance, standIn.gameObject);
        NetworkedItem made = null;
        if (update.UpdateType != ItemUpdate.ItemUpdateType.Destroy && !update.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create))
            made = MakeFromRow(new ItemUpdate
            {
                ItemNetId = standIn.NetId,
                PrefabName = standIn.PrefabName,
                AuthorityRevision = standIn.Revision
            }, standIn.State);
        standIn.Retire();
        InventoryRows.Remove(update.ItemNetId);
        return made;
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

    private static void RetireStandIns()
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        if (inventory != null)
            foreach (InventoryStandIn standIn in InventoryStandIn.All)
                VanillaInventorySlots.Forget(inventory, standIn.gameObject);
        InventoryStandIn.RetireAll();
    }
}
