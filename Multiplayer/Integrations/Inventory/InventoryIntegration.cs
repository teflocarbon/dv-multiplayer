using DV.InventorySystem;
using DV.UI.Inventory;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Core.Items;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data.Items;
using System;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Integrations.Inventory;

internal readonly struct InventorySlotRuntimeSnapshot
{
    public InventorySlotRuntimeSnapshot(int absoluteSlot, GameObject itemObject,
        NetworkedItem networkedItem, bool dropped, bool reserved)
    {
        AbsoluteSlot = absoluteSlot;
        ItemObject = itemObject;
        NetworkedItem = networkedItem;
        Dropped = dropped;
        Reserved = reserved;
    }

    public int AbsoluteSlot { get; }
    public GameObject ItemObject { get; }
    public NetworkedItem NetworkedItem { get; }
    public bool Dropped { get; }
    public bool Reserved { get; }
}

internal readonly struct InventoryClaimExecution
{
    public InventoryClaimExecution(InventoryClaimPlan plan, bool succeeded,
        string failureReason, int currentSlot, bool reserved, bool dropped)
    {
        Plan = plan;
        Succeeded = succeeded;
        FailureReason = failureReason ?? string.Empty;
        CurrentSlot = currentSlot;
        Reserved = reserved;
        Dropped = dropped;
    }

    public InventoryClaimPlan Plan { get; }
    public bool Succeeded { get; }
    public string FailureReason { get; }
    public int CurrentSlot { get; }
    public bool Reserved { get; }
    public bool Dropped { get; }
}

internal readonly struct InventoryRemovalSnapshot
{
    public InventoryRemovalSnapshot(int slotBefore, int equippedSlotBefore, bool containedBefore,
        int slotAfter, int equippedSlotAfter, bool containedAfter)
    {
        SlotBefore = slotBefore;
        EquippedSlotBefore = equippedSlotBefore;
        ContainedBefore = containedBefore;
        SlotAfter = slotAfter;
        EquippedSlotAfter = equippedSlotAfter;
        ContainedAfter = containedAfter;
    }

    public int SlotBefore { get; }
    public int EquippedSlotBefore { get; }
    public bool ContainedBefore { get; }
    public int SlotAfter { get; }
    public int EquippedSlotAfter { get; }
    public bool ContainedAfter { get; }
}

internal interface IInventoryRuntimeAdapter
{
    byte LocalPlayerId { get; }
    bool RuntimeActive { get; }
    bool TrySnapshotSlot(InventoryUIController controller, int relativeSlot,
        InventorySectionController section, out InventorySlotRuntimeSnapshot snapshot);
    bool ContainsActive(GameObject item);
    int AddActive(GameObject item, int preferredSlot = -1);
    InventoryClaimExecution EnsureDroppedClaim(GameObject item, int expectedSlot);
    InventoryClaimExecution RestoreClaim(GameObject item, int expectedSlot);
    InventoryRemovalSnapshot RevokeMembership(GameObject item, ushort itemNetId = 0);
    void PurgeForeignDroppedClaims(string reason);
    void PurgeContainerMembership(GameObject item);
}

internal sealed class DerailValleyInventoryRuntimeAdapter : IInventoryRuntimeAdapter
{
    private bool purgingForeignDroppedClaims;

    public byte LocalPlayerId => NetworkLifecycle.Instance == null ? (byte)0 :
        NetworkLifecycle.Instance.IsHost()
            ? NetworkLifecycle.Instance.Server?.SelfId ?? 0
            : NetworkLifecycle.Instance.Client?.PlayerId ?? 0;

    public bool RuntimeActive => NetworkLifecycle.Instance != null &&
        (NetworkLifecycle.Instance.IsClientRunning || NetworkLifecycle.Instance.IsHost());

    public bool TrySnapshotSlot(InventoryUIController controller, int relativeSlot,
        InventorySectionController section, out InventorySlotRuntimeSnapshot snapshot)
    {
        snapshot = default;
        if (controller?.provider?.Inventory == null || section == null)
            return false;

        int absoluteSlot = controller.GetAbsoluteSlotIndex(relativeSlot,
            section == controller.hotbarController);
        GameObject itemObject = controller.provider.Inventory.PeekItemAtSlot(absoluteSlot, true);
        NetworkedItem item = itemObject != null ? itemObject.GetComponent<NetworkedItem>() : null;
        snapshot = new InventorySlotRuntimeSnapshot(absoluteSlot, itemObject, item,
            controller.provider.Inventory.GetSlotDroppedState(absoluteSlot),
            controller.provider.Inventory.GetSlotReservedState(absoluteSlot));
        return true;
    }

    public bool ContainsActive(GameObject item)
    {
        if (item == null)
            return false;
        try { return DV.InventorySystem.Inventory.Instance?.Contains(item, false) == true; }
        catch { return false; }
    }

    public int AddActive(GameObject item, int preferredSlot = -1)
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        if (inventory == null || item == null)
            return -1;
        int currentSlot = inventory.IndexOf(item);
        if (currentSlot >= 0 && inventory.Contains(item, false))
            return currentSlot;
        return preferredSlot >= 0
            ? inventory.AddItemToInventory(item, preferredSlot, false)
            : inventory.AddItemToInventory(item, false);
    }

    public InventoryClaimExecution EnsureDroppedClaim(GameObject item, int expectedSlot)
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        RuntimeInventoryView view = new(inventory, item, expectedSlot);
        InventoryClaimPlan plan = InventoryClaimPlanner.EnsureDroppedClaim(view, expectedSlot);
        string failure = string.Empty;
        bool succeeded = !plan.Rejected;
        if (succeeded)
        {
            switch (plan.Action)
            {
                case InventoryClaimAction.None:
                    break;
                case InventoryClaimAction.DropExistingItemInPlace:
                    succeeded = inventory.DropItemFromHandsOrInventory(item) != null;
                    failure = succeeded ? string.Empty : "essential-claim-drop-failed";
                    break;
                case InventoryClaimAction.AddToExpectedSlotThenDrop:
                    succeeded = inventory.AddItemToInventory(item, plan.TargetSlot, false) >= 0 &&
                        inventory.DropItemFromHandsOrInventory(item) != null;
                    failure = succeeded ? string.Empty : "essential-claim-repair-failed";
                    break;
                default:
                    succeeded = false;
                    failure = "essential-claim-plan-invalid";
                    break;
            }
        }
        return ClaimResult(inventory, item, plan, succeeded,
            plan.Rejected ? plan.Reason : failure);
    }

    public InventoryClaimExecution RestoreClaim(GameObject item, int expectedSlot)
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        RuntimeInventoryView view = new(inventory, item, expectedSlot);
        InventoryClaimPlan plan = InventoryClaimPlanner.RestoreClaim(view, expectedSlot);
        bool succeeded = !plan.Rejected;
        string failure = plan.Rejected ? plan.Reason : string.Empty;
        if (succeeded && plan.Action is InventoryClaimAction.RestoreExistingItem or
            InventoryClaimAction.AddToExpectedSlot)
        {
            succeeded = inventory.AddItemToInventory(item, plan.TargetSlot, false) >= 0;
            if (!succeeded)
                failure = "recall-claim-restore-failed";
        }
        return ClaimResult(inventory, item, plan, succeeded, failure);
    }

    public InventoryRemovalSnapshot RevokeMembership(GameObject item, ushort itemNetId = 0)
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        if (inventory == null || item == null)
            return new InventoryRemovalSnapshot(-1, -1, false, -1, -1, false);
        using IDisposable authoritativeRemoval = InventoryIntegration.BeginAuthoritativeRemoval();
        int slotBefore = inventory.IndexOf(item);
        int equippedBefore = inventory.GetEquipSlotForItem(item);
        bool containedBefore = inventory.Contains(item, true);

        // A stale network projection may leave a different Unity representation in the
        // inventory slot than the one selected by the NetId registry. Purge every matching
        // representation, otherwise the foreign dropped/reserved entry remains as a red
        // silhouette and permanently consumes the slot.
        GameObject[] inventoryItems = inventory.GetItemsArray(true) ?? [];
        GameObject[] candidates = inventoryItems
            .Append(item)
            .Where(candidate => candidate != null &&
                (candidate == item || itemNetId != 0 &&
                    candidate.GetComponent<NetworkedItem>()?.NetId == itemNetId))
            .Distinct()
            .ToArray();
        foreach (GameObject candidate in candidates)
        {
            inventory.ItemContainerRegistry?.PurgeItemFromContainer(candidate);
            int equippedSlot = inventory.GetEquipSlotForItem(candidate);
            if (equippedSlot >= 0)
                inventory.DropItemFromHandsOrInventory(candidate);

            int slot = inventory.IndexOf(candidate);
            if (slot >= 0)
            {
                // Reserved/dropped silhouettes are removed by PurgeFromInventory. Ordinary
                // active entries are removed through the game's drop transition with
                // keepInactive=true, which clears the slot without projecting the item into
                // the world. Storage side effects are suppressed by the authoritative-removal
                // scope and reconciled explicitly by the caller.
                if (!inventory.PurgeFromInventory(candidate))
                    inventory.DropItemFromHandsOrInventory(slot, true);
                if (inventory.IndexOf(candidate) >= 0 || inventory.Contains(candidate, true))
                {
                    inventory.PurgeFromInventory(candidate);
                    int remainingSlot = inventory.IndexOf(candidate);
                    if (remainingSlot >= 0)
                        inventory.DropItemFromHandsOrInventory(remainingSlot, true);
                }
            }
        }
        return new InventoryRemovalSnapshot(slotBefore, equippedBefore, containedBefore,
            inventory.IndexOf(item), inventory.GetEquipSlotForItem(item),
            candidates.Any(candidate => inventory.Contains(candidate, true) ||
                inventory.IndexOf(candidate) >= 0 || inventory.GetEquipSlotForItem(candidate) >= 0));
    }

    public void PurgeForeignDroppedClaims(string reason)
    {
        if (purgingForeignDroppedClaims)
            return;
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        byte localPlayerId = LocalPlayerId;
        if (inventory == null || localPlayerId == 0)
            return;

        purgingForeignDroppedClaims = true;
        try
        {
            foreach (GameObject candidate in (inventory.GetItemsArray(true) ?? [])
                         .Where(value => value != null).Distinct().ToArray())
            {
                int slot = inventory.IndexOf(candidate);
                if (slot < 0 || !inventory.GetSlotDroppedState(slot) ||
                    !inventory.GetSlotReservedState(slot) && !inventory.GetSlotLockState(slot))
                    continue;
                NetworkedItem networkedItem = candidate.GetComponent<NetworkedItem>();
                bool localPossessionActive = inventory.GetEquipSlotForItem(candidate) >= 0 ||
                    networkedItem?.Item?.IsGrabbed() == true;
                bool retrievalClaim = networkedItem != null &&
                    networkedItem.InventoryClaimSlot >= 0 &&
                    (networkedItem.InventoryClaimFlags & (ItemInventoryClaimFlags.Reserved |
                        ItemInventoryClaimFlags.Locked)) != 0;
                if (networkedItem == null ||
                    !InventoryPresentationPlanner.ShouldPurgeForeignDroppedClaim(
                        new InventoryPresentationInput(networkedItem.NetId,
                            networkedItem.PersistentOwnerPlayerId, localPlayerId,
                            networkedItem.Item?.InventorySpecs?.IsEssential == true,
                            retrievalClaim, NetworkedLostAndFoundManager.Contains(networkedItem.NetId)),
                        localSlotDropped: true, localSlotReservedOrLocked: true,
                        localPossessionActive: localPossessionActive))
                    continue;

                InventoryRemovalSnapshot removal = RevokeMembership(candidate, networkedItem.NetId);
                DebugRuntime.Publish("inventory", "item.foreign-dropped-silhouette-purged",
                    NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
                    removal.ContainedAfter ? DebugSeverity.Warning : DebugSeverity.Info,
                    "Item", networkedItem.NetId.ToString(), new()
                    {
                        ["reason"] = reason ?? string.Empty,
                        ["localPlayerId"] = localPlayerId,
                        ["persistentOwnerPlayerId"] = networkedItem.PersistentOwnerPlayerId,
                        ["slotBefore"] = slot,
                        ["slotAfter"] = removal.SlotAfter,
                        ["containedAfter"] = removal.ContainedAfter
                    });
            }
        }
        finally
        {
            purgingForeignDroppedClaims = false;
        }
    }

    public void PurgeContainerMembership(GameObject item) =>
        DV.InventorySystem.Inventory.Instance?.ItemContainerRegistry?.PurgeItemFromContainer(item);

    private static InventoryClaimExecution ClaimResult(DV.InventorySystem.Inventory inventory,
        GameObject item, InventoryClaimPlan plan, bool succeeded, string failure)
    {
        int slot = inventory?.IndexOf(item) ?? -1;
        return new InventoryClaimExecution(plan, succeeded, failure, slot,
            slot >= 0 && inventory.GetSlotReservedState(slot),
            slot >= 0 && inventory.GetSlotDroppedState(slot));
    }

    private sealed class RuntimeInventoryView : IInventoryView
    {
        private readonly DV.InventorySystem.Inventory inventory;
        private readonly GameObject item;
        private readonly int expectedSlot;

        public RuntimeInventoryView(DV.InventorySystem.Inventory inventory, GameObject item,
            int expectedSlot)
        {
            this.inventory = inventory;
            this.item = item;
            this.expectedSlot = expectedSlot;
        }

        public bool IsAvailable => inventory != null && item != null;
        public int CurrentSlot => inventory?.IndexOf(item) ?? -1;
        public bool ContainsActiveItem => inventory?.Contains(item, false) == true;
        public bool IsExpectedSlotOccupiedByOther
        {
            get
            {
                if (inventory == null || expectedSlot < 0)
                    return false;
                GameObject occupant = inventory.PeekItemAtSlot(expectedSlot, true);
                return occupant != null && occupant != item;
            }
        }
        public bool CurrentSlotReserved => CurrentSlot >= 0 &&
            inventory.GetSlotReservedState(CurrentSlot);
        public bool CurrentSlotDropped => CurrentSlot >= 0 &&
            inventory.GetSlotDroppedState(CurrentSlot);
    }
}

/// <summary>
/// Stable multiplayer boundary for Derail Valley inventory reads, recall routing, and slot
/// presentation. Harmony patches should forward here rather than reproduce inventory policy.
/// </summary>
internal static class InventoryIntegration
{
    private static readonly IInventoryRuntimeAdapter runtime =
        new DerailValleyInventoryRuntimeAdapter();
    private static int authoritativeRemovalDepth;

    internal static bool AuthoritativeRemovalActive => authoritativeRemovalDepth > 0;

    internal static IDisposable BeginAuthoritativeRemoval()
    {
        authoritativeRemovalDepth++;
        return new AuthoritativeRemovalScope();
    }

    private sealed class AuthoritativeRemovalScope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            authoritativeRemovalDepth = Math.Max(0, authoritativeRemovalDepth - 1);
        }
    }
    private static uint lostItemRequestId;

    public static byte LocalPlayerId => runtime.LocalPlayerId;

    public static bool ContainsActive(GameObject item) => runtime.ContainsActive(item);
    public static int AddActive(GameObject item, int preferredSlot = -1) =>
        runtime.AddActive(item, preferredSlot);
    public static InventoryClaimExecution EnsureDroppedClaim(GameObject item, int expectedSlot) =>
        runtime.EnsureDroppedClaim(item, expectedSlot);
    public static InventoryClaimExecution RestoreClaim(GameObject item, int expectedSlot) =>
        runtime.RestoreClaim(item, expectedSlot);
    public static InventoryRemovalSnapshot RevokeMembership(GameObject item, ushort itemNetId = 0) =>
        runtime.RevokeMembership(item, itemNetId);
    public static void PurgeForeignDroppedClaims(string reason) =>
        runtime.PurgeForeignDroppedClaims(reason);
    public static void PurgeContainerMembership(GameObject item) =>
        runtime.PurgeContainerMembership(item);

    public static InventoryItemPresentation Project(NetworkedItem item, bool isLostAndFound = false)
    {
        bool retrievalEligible = item?.Item?.InventorySpecs?.IsEssential == true;
        bool hasRetrievalClaim = item != null && item.InventoryClaimSlot >= 0 &&
            (item.InventoryClaimFlags & (ItemInventoryClaimFlags.Reserved |
                ItemInventoryClaimFlags.Locked)) != 0;
        return InventoryPresentationPlanner.Plan(new InventoryPresentationInput(
            item?.NetId ?? 0, item?.PersistentOwnerPlayerId ?? 0, runtime.LocalPlayerId,
            retrievalEligible, hasRetrievalClaim, isLostAndFound));
    }

    /// <returns>True when the original Derail Valley Get handler should execute.</returns>
    public static bool HandleGetClicked(InventoryUIController controller, int relativeSlot,
        InventorySectionController section)
    {
        global::Multiplayer.Multiplayer.Log($"[Inventory Integration] Get clicked: " +
            $"relativeSlot={relativeSlot}, section={section?.section.ToString() ?? "null"}, " +
            $"runtimeActive={runtime.RuntimeActive}");
        if (!runtime.RuntimeActive ||
            !runtime.TrySnapshotSlot(controller, relativeSlot, section, out InventorySlotRuntimeSnapshot slot))
            return true;

        NetworkedItem item = slot.NetworkedItem;
        LostItemData lost = item == null ? null : NetworkedLostAndFoundManager.ClientItems
            .FirstOrDefault(entry => entry?.NetId == item.NetId);
        InventoryItemPresentation presentation = Project(item, lost != null);

        global::Multiplayer.Multiplayer.Log($"[Inventory Integration] Slot snapshot: " +
            $"absoluteSlot={slot.AbsoluteSlot}, dropped={slot.Dropped}, reserved={slot.Reserved}, " +
            $"item={slot.ItemObject?.name ?? "null"}, netId={item?.NetId ?? 0}, " +
            $"owner=P{item?.PersistentOwnerPlayerId ?? 0}, local=P{runtime.LocalPlayerId}, " +
            $"route={presentation.RecallRoute}");

        if (presentation.RecallRoute == InventoryRecallRoute.Vanilla)
            return true;
        if (presentation.RecallRoute == InventoryRecallRoute.BlockForeignOwner)
        {
            DebugRuntime.Publish("item", "item.foreign-item-recall-blocked",
                DebugRuntimeSide.Client, DebugSeverity.Warning, "Item", item.NetId.ToString(), new()
                {
                    ["localPlayerId"] = runtime.LocalPlayerId,
                    ["persistentOwnerPlayerId"] = item.PersistentOwnerPlayerId,
                    ["slot"] = slot.AbsoluteSlot
                });
            return false;
        }
        if (presentation.RecallRoute == InventoryRecallRoute.BlockNotRecallable)
        {
            DebugRuntime.Publish("item", "item.recall-blocked-not-eligible",
                DebugRuntimeSide.Client, DebugSeverity.Warning, "Item", item.NetId.ToString(), new()
                {
                    ["localPlayerId"] = runtime.LocalPlayerId,
                    ["persistentOwnerPlayerId"] = item.PersistentOwnerPlayerId,
                    ["isEssential"] = item.Item?.InventorySpecs?.IsEssential == true,
                    ["slot"] = slot.AbsoluteSlot
                });
            return false;
        }

        if (presentation.RecallRoute == InventoryRecallRoute.LostAndFoundRetrieval &&
            !NetworkLifecycle.Instance.IsHost())
        {
            uint requestId = unchecked(++lostItemRequestId);
            NetworkLifecycle.Instance.Client?.RequestLostItemRetrieval(requestId, lost.Handle,
                item.NetId, lost.Revision, slot.AbsoluteSlot);
            return false;
        }

        NetworkedItemManager.Instance?.RequestItemRecall(item, slot.AbsoluteSlot);
        return false;
    }
}
