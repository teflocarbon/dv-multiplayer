using System.Collections.Generic;
using System.Linq;
using Multiplayer.Client.Items.LiveState;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Client.Items.Projection;

internal sealed partial class ClientItemManager
{
    // The predicted take-out (docs/design/items/inventory.md, "Taking out"; D14, OI-COLD-10): the
    // player takes an item out of a slot (the hotbar's unstash, the UI's equip or drop, the VR
    // backpack, a move onto the VR belt), and its object is made from the row in that frame, under
    // its NetId and with its state, parked as vanilla parks a stashed item, and swapped in for the
    // stand-in before vanilla acts. Vanilla then equips or drops it as it would any item, and the
    // object's own change goes up as the request (a take, a drop). The server confirms it, or
    // refuses: a refusal with a correction puts the item back in its slot as that update does
    // (ReceiveAsStandIn), and one without is rolled back here from what the take-out kept.

    // How long a take-out waits for its answer before it's no longer rolled back.
    private const float TakeOutAnswerSeconds = 15f;

    private sealed class PendingTakeOut
    {
        public ItemUpdate Slot;
        public string State;
        public float Since;
    }

    private readonly Dictionary<ushort, PendingTakeOut> pendingTakeOuts = new();

    /// <summary>
    /// Takes a stand-in's item out: its object is made from the row, parked, and put where vanilla
    /// had the stand-in, which goes. The object, or null if it couldn't be made (the stand-in stays).
    /// </summary>
    public NetworkedItem TakeOut(InventoryStandIn standIn, string reason)
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        if (inventory == null || standIn == null || !standIn.Known)
            return null;
        int slot = inventory.IndexOf(standIn.gameObject);
        ItemUpdate inSlot = new()
        {
            UpdateType = ItemUpdate.ItemUpdateType.ItemState,
            ItemNetId = standIn.NetId,
            PrefabName = standIn.PrefabName,
            AuthorityRevision = standIn.Revision,
            ItemState = ItemState.InInventory,
            PlayerId = NetworkRole.LocalPlayerId,
            InventorySlot = slot,
            InventoryClaimSlot = standIn.ClaimSlot,
            InventoryClaimFlags = standIn.ClaimFlags,
            Relation = standIn.Relation
        };
        NetworkedItem item = MakeFromRow(inSlot, standIn.State);
        if (item == null)
            return null;
        // What the server last said of it, so the object's own request carries the right revision.
        item.ApplyAuthorityMetadata(inSlot);
        inventory.FinalizeRemoveItemFromWorld(item.gameObject);
        VanillaInventorySlots.Rebind(inventory, standIn.gameObject, item.gameObject);
        pendingTakeOuts[standIn.NetId] = new PendingTakeOut
        {
            Slot = inSlot,
            State = standIn.State,
            Since = Time.realtimeSinceStartup
        };
        NetworkedItemManager.TraceItem("item.taken-out-of-stand-in", item, new()
        {
            ["reason"] = reason ?? string.Empty,
            ["slot"] = slot,
            ["stateLength"] = standIn.State.Length
        });
        standIn.Retire();
        return item;
    }

    /// <summary>The server's answer to a request: a refused take-out it sent no correction for is rolled back.</summary>
    internal void ReceiveTakeOutResult(ClientboundItemResultPacket result)
    {
        float now = Time.realtimeSinceStartup;
        foreach (ushort stale in pendingTakeOuts.Where(pair => now - pair.Value.Since > TakeOutAnswerSeconds)
                     .Select(pair => pair.Key).ToArray())
            pendingTakeOuts.Remove(stale);
        if (result == null || result.ItemNetId == 0 || !pendingTakeOuts.TryGetValue(result.ItemNetId, out PendingTakeOut pending))
            return;
        pendingTakeOuts.Remove(result.ItemNetId);
        if (result.Accepted || InventoryStandIn.TryGet(result.ItemNetId, out InventoryStandIn back) && !InventorySilhouettes.IsSilhouette(back))
            return;
        Multiplayer.Log($"Inventory: taking {pending.Slot.PrefabName} (netId {result.ItemNetId}) out was refused " +
                        $"({result.Refusal}, {result.Detail}); it goes back in slot {pending.Slot.InventorySlot}");
        ReceiveAsStandIn(pending.Slot, pending.State, rollBack: true);
    }

    /// <summary>An object vanilla has stored in a slot (moved off the VR belt) becomes its stand-in there; the move was the request.</summary>
    public void StowWithoutRequest(NetworkedItem item)
    {
        InventoryStandIn standIn = InventoryStandIn.ForObject(item);
        VanillaInventorySlots.Rebind(DV.InventorySystem.Inventory.Instance, item.gameObject, standIn.gameObject);
        item.BeginColdStorageRetirement();
        RetireClientProjection(item, "stored-off-vr-belt");
    }

    // The object of a take-out the server didn't take is let go and goes; its stand-in comes back.
    private static void ReleaseForRollBack(NetworkedItem item)
    {
        item.BeginColdStorageRetirement();
        if (item.Item != null && item.Item.IsGrabbed())
            item.Item.ForceEndInteraction();
        VanillaInventorySlots.Forget(DV.InventorySystem.Inventory.Instance, item.gameObject);
    }
}
