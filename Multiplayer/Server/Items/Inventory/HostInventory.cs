using System;
using System.Collections.Generic;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Server.Items.Inventory;

/// <summary>
/// The host's own player as a row holder (docs/design/items/inventory.md, "Stand-ins"; D14,
/// OI-COLD-11): on a player host its slots hold stand-ins as a client's do, and the server keeps
/// those items as data only. Everything is in process and synchronous, so nothing is predicted:
/// <list type="bullet">
/// <item>a put-away commits the stash, and the object goes to data with the state it had
/// (<see cref="PlayerItems.StoreAsData"/>);</item>
/// <item>a take-out makes the object from its row in that frame, the one object that is both the
/// server's and the player's, and the host's observed change (ProcessChanged) commits the take;</item>
/// <item>an object that reaches a slot any other way (the server's own flows, vanilla adding one
/// directly: a purchase, a reprinted booklet, a starting item) is put away by the sweep once it has
/// settled, as the server's object of a remote player's item goes.</item>
/// </list>
/// A dedicated host has no player and so no row holder.
/// </summary>
internal sealed class HostInventory : IInventoryRowHolder
{
    // How long an object sits in one of the host's slots, unchanged, before the sweep puts it away.
    private const float SettleSeconds = 1f;

    public static readonly HostInventory Instance = new();

    private static readonly Dictionary<ushort, float> settlingSince = new();

    private static ServerPlayer Player => HostItemActor.Player;

    public bool Active => NetworkLifecycle.Instance?.IsHost() == true && Player != null && ServerLedger.Loaded &&
                          DV.InventorySystem.Inventory.Instance != null;

    public bool CatchesDirectAdds => false;

    /// <summary>Whether the host's stand-in holds this item: it's kept as data in the host's own slot.</summary>
    public static bool Holds(ushort netId) =>
        Instance.Active && InventoryStandIn.TryGet(netId, out _) &&
        LiveItems.TryGet(netId, out LiveItems.LiveItem record) && record.Holder == Player.Guid;

    public bool PutsAwayAsStandIn(NetworkedItem item) =>
        Active && item != null && item.NetId != 0 && item.Item != null &&
        !item.ColdMaterializationPending && !item.ColdStorageRetirement && !ItemSaveLoad.IsRestorePending(item) &&
        LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record) && record.PersistentItemId != Guid.Empty &&
        record.Placement is not (ItemPlacementKind.Destroyed or ItemPlacementKind.Installed or ItemPlacementKind.LostAndFound) &&
        !LostAndFoundServer.Contains(item.NetId) &&
        InventoryStandIn.CanStandIn(item.gameObject) &&
        !DV.InventorySystem.Inventory.Instance.IsDestroyedOnAddedToInventory(item.gameObject);

    /// <summary>
    /// The host's player put an object away and vanilla has its stand-in in a slot: the stash is
    /// committed as the host's own change (unless the server has it there already), and the item
    /// goes to data with the state it had. If either is refused the object goes back in the slot.
    /// </summary>
    public void CommitPutAway(NetworkedItem item, InventoryStandIn standIn)
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        ServerPlayer host = Player;
        int slot = inventory.IndexOf(standIn.gameObject);
        settlingSince.Remove(item.NetId);
        if (host == null || slot < 0 || !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record))
        {
            Undo(inventory, item, standIn, "no record or slot");
            return;
        }
        if (record.Placement != ItemPlacementKind.PlayerInventory || record.Holder != host.Guid || record.Slot != slot)
        {
            ItemUpdate snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.FullSync);
            if (snapshot == null)
            {
                Undo(inventory, item, standIn, "no snapshot");
                return;
            }
            snapshot.ItemState = ItemState.InInventory;
            snapshot.PlayerId = host.PlayerId;
            snapshot.PlayerHand = PlayerHand.None;
            snapshot.InventorySlot = slot;
            snapshot.InventoryClaimSlot = record.InventoryClaimSlot;
            snapshot.AuthorityRevision = record.Revision;
            if (!LiveItems.TryApplyTransition(item, snapshot, host, ItemTransitionReason.HostLocalState, true,
                    out string rejection, cause: ItemChangeLog.Move(ItemMoveReason.StoredInInventory, host, "slot " + slot)))
            {
                Undo(inventory, item, standIn, rejection);
                return;
            }
            item.ApplyHostLocalCanonicalTransition(snapshot, host);
        }
        string state = standIn.State;
        if (item.Item.IsGrabbed())
            item.Item.ForceEndInteraction();
        if (!PlayerItems.StoreAsData(record, item, host, state))
        {
            Undo(inventory, item, standIn, "it stays an object");
            return;
        }
        standIn.Relation = item.Relation;
        standIn.ClaimSlot = record.InventoryClaimSlot;
        standIn.ClaimFlags = record.InventoryClaimFlags;
        standIn.Revision = record.Revision;
        NetworkedItemManager.TraceItem("item.put-away-as-stand-in", item, new()
        {
            ["slot"] = slot,
            ["host"] = true,
            ["stateLength"] = state.Length
        });
    }

    // The object goes back where vanilla had its stand-in: an object in a slot, as before D14.
    private static void Undo(DV.InventorySystem.Inventory inventory, NetworkedItem item, InventoryStandIn standIn, string why)
    {
        Multiplayer.LogWarning($"Inventory: the host's {standIn.PrefabName} (netId {standIn.NetId}) stays an object in its slot: {why}");
        VanillaInventorySlots.Rebind(inventory, standIn.gameObject, item.gameObject);
        standIn.Retire();
    }

    public NetworkedItem TakeOut(InventoryStandIn standIn, string reason) =>
        standIn != null && standIn.Known && Active ? MakeInSlot(standIn.NetId, reason) : null;

    /// <summary>
    /// The object of an item the host's stand-in holds, made from its row in this frame and put in
    /// the stand-in's place, parked as vanilla parks a stashed item; the stand-in goes. Vanilla (a
    /// take-out) or a request then acts on it, and one left in its slot is put away again by the
    /// sweep. Null if it couldn't be made (the stand-in stays).
    /// </summary>
    internal static NetworkedItem MakeInSlot(ushort netId, string reason)
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        if (inventory == null || !InventoryStandIn.TryGet(netId, out InventoryStandIn standIn))
            return null;
        int slot = inventory.IndexOf(standIn.gameObject);
        NetworkedItem item = PlayerItems.MakeNow(netId, Player);
        if (item == null)
            return null;
        inventory.FinalizeRemoveItemFromWorld(item.gameObject);
        VanillaInventorySlots.Rebind(inventory, standIn.gameObject, item.gameObject);
        settlingSince.Remove(netId);
        NetworkedItemManager.TraceItem("item.taken-out-of-stand-in", item, new()
        {
            ["reason"] = reason ?? string.Empty,
            ["slot"] = slot,
            ["host"] = true,
            ["stateLength"] = standIn.State.Length
        });
        standIn.Retire();
        return item;
    }

    public void StowWithoutRequest(NetworkedItem item)
    {
        InventoryStandIn standIn = InventoryStandIn.ForObject(item);
        VanillaInventorySlots.Rebind(DV.InventorySystem.Inventory.Instance, item.gameObject, standIn.gameObject);
        CommitPutAway(item, standIn);
    }

    /// <summary>The host's join: a row taken up as data is drawn as its stand-in in its slot.</summary>
    internal static void Draw(ItemUpdate row, string state)
    {
        InventoryStandIn standIn = InventoryStandIn.For(row.ItemNetId, row.PrefabName);
        standIn.Relation = row.Relation;
        standIn.ClaimSlot = row.InventoryClaimSlot;
        standIn.ClaimFlags = row.InventoryClaimFlags;
        standIn.Revision = row.AuthorityRevision;
        standIn.State = state ?? string.Empty;
        standIn.Place(row);
    }

    /// <summary>
    /// Host, from the data-only scan: any object of the host's own player's in one of its slots
    /// (not a silhouette, not on an enabled VR belt slot) that the server has there, and that has
    /// sat there a second, is put away as a stand-in.
    /// </summary>
    internal static void Sweep(float now)
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        ServerPlayer host = Player;
        if (!Instance.Active || host == null)
        {
            settlingSince.Clear();
            return;
        }
        HashSet<ushort> seen = new();
        for (int slot = 0; slot < inventory.InventoryItems.Length; slot++)
        {
            GameObject gameObject = VanillaInventorySlots.At(inventory, slot);
            if (gameObject == null || InventoryStandIn.Is(gameObject) || inventory.GetSlotDroppedState(slot) ||
                !gameObject.TryGetComponent(out NetworkedItem item) || item.Item == null ||
                inventory.IsValidVRBeltIndex(slot) && item.Item.IsBeltSnappable || !Instance.PutsAwayAsStandIn(item) ||
                !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record) ||
                record.Placement != ItemPlacementKind.PlayerInventory || record.Holder != host.Guid || record.Slot != slot)
                continue;
            seen.Add(item.NetId);
            if (!settlingSince.TryGetValue(item.NetId, out float since))
            {
                settlingSince[item.NetId] = now;
                continue;
            }
            if (now - since < SettleSeconds)
                continue;
            settlingSince.Remove(item.NetId);
            Instance.StowWithoutRequest(item);
        }
        foreach (ushort gone in new List<ushort>(settlingSince.Keys))
            if (!seen.Contains(gone))
                settlingSince.Remove(gone);
    }

    internal static void Clear() => settlingSince.Clear();
}
