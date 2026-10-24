using System;
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
/// <item>an object that reaches a slot any other way (the server's own flows: a recall, a Lost and
/// Found retrieval; vanilla adding one directly: a purchase) is put away once the server has
/// committed it there (<see cref="Stow"/>, from <see cref="PlayerItems.Stored"/>), as the server's
/// object of a remote player's item goes;</item>
/// <item>starting items, grants and withdrawals are made as rows, drawn as stand-ins with no object
/// (<see cref="Draw"/>).</item>
/// </list>
/// A dedicated host has no player and so no row holder.
/// </summary>
internal sealed class HostInventory : IInventoryRowHolder
{
    public static readonly HostInventory Instance = new();

    private static ServerPlayer Player => HostItemActor.Player;

    public bool Active => NetworkLifecycle.Instance?.IsHost() == true && Player != null && ServerLedger.Loaded &&
                          DV.InventorySystem.Inventory.Instance != null;

    public bool CatchesDirectAdds => false;

    /// <summary>Whether the host's stand-in holds this item: it's kept as data in the host's own slot.</summary>
    public static bool Holds(ushort netId) =>
        Instance.Active && InventoryStandIn.TryGet(netId, out InventoryStandIn standIn) && !InventorySilhouettes.IsSilhouette(standIn) &&
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
            TellKnowers(item, record, host);
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
    // The stash goes to every game that was shown the item: its object goes to data in this frame,
    // before the server's tick would send the change, and an owner whose silhouette stands for it
    // is told nowhere else (StoreAsData tells only the others that it's gone).
    private static void TellKnowers(NetworkedItem item, LiveItems.LiveItem record, ServerPlayer host)
    {
        ItemUpdate stashed = item.CreateUpdateData(ItemUpdate.ItemUpdateType.FullSync);
        if (stashed == null)
            return;
        LiveItems.WriteToSnapshot(record, stashed, ItemTransitionReason.HostLocalState);
        stashed.ItemState = ItemState.InInventory;
        foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
            if (player != host && player.Peer != NetworkServer.Instance.SelfPeer && player.KnownItems.ContainsKey(item))
                NetworkServer.Instance.SendItemUpdate(stashed, player);
    }

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
    /// take-out) or a request then acts on it, and one left in its slot is put away again a moment
    /// later (PlayerItems.WithObject). Null if it couldn't be made (the stand-in stays).
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

    /// <summary>
    /// A row the server keeps as data in one of the host's slots (the host's join, a starting item,
    /// a grant, a withdrawal) is drawn as its stand-in there.
    /// </summary>
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
    /// The server has committed an object of the host's own player's into one of its slots (from
    /// <see cref="PlayerItems.Stored"/>): once vanilla has it there, it's put away as a stand-in.
    /// Not a silhouette, nor on an enabled VR belt slot (OI-COLD-14), nor anything that stays an
    /// object (<see cref="PutsAwayAsStandIn"/>). False to look again next frame: vanilla hasn't
    /// added it to that slot yet.
    /// </summary>
    internal static bool Stow(NetworkedItem item, LiveItems.LiveItem record)
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        ServerPlayer host = Player;
        if (!Instance.Active || host == null)
            return inventory != null;
        if (record.Holder != host.Guid || record.Slot < 0 || !Instance.PutsAwayAsStandIn(item))
            return true;
        if (VanillaInventorySlots.At(inventory, record.Slot) != item.gameObject)
            return false;
        if (inventory.GetSlotDroppedState(record.Slot) ||
            inventory.IsValidVRBeltIndex(record.Slot) && item.Item.IsBeltSnappable ||
            inventory.GetEquipSlotForItem(item.gameObject) >= 0)
            return true;
        Instance.StowWithoutRequest(item);
        return true;
    }
}
