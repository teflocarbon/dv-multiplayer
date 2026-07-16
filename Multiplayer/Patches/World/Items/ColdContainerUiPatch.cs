using DV.InventorySystem;
using DV.Items;
using DV.UI.Inventory;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Integrations.Inventory;
using UnityEngine;

namespace Multiplayer.Patches.World.Items;

[HarmonyPatch]
public static class ColdContainerUiPatch
{
    // Non-VR quick-move is the destructive shift-click path. Its base implementation calls
    // DropItemFromHandsOrInventory before AddItem, so it must be intercepted as one atomic
    // cold-container request. OnSlotClicked is too early and only broadcasts SlotClicked;
    // the quick-move subscriber runs separately from that event.
    [HarmonyPatch(typeof(InventoryViewNonVR), "QuickMoveAction")]
    [HarmonyPrefix]
    private static bool QuickMoveAction(int __0, bool __1, bool __2)
    {
        int slotIndex = __0;
        bool isHandSlot = __1;
        bool isContainerSlot = __2;
        AItemContainer active = Inventory.Instance?.ItemContainerRegistry?.ActiveContainer;
        if (active == null || !ColdContainerUiIntegration.Active || isHandSlot)
            return true;

        if (isContainerSlot)
            return !ColdContainerUiIntegration.TryInterceptRemove(active, slotIndex);

        GameObject item = Inventory.Instance.PeekItemAtSlot(slotIndex, true);
        return !ColdContainerUiIntegration.TryInterceptAdd(active, item, -1);
    }

    // Intercept the UI transaction before Derail Valley removes the source item. The
    // base implementation calls DropItemFromHandsOrInventory before ItemContainer.AddItem,
    // which is too late for the host-authoritative cold-container transaction.
    [HarmonyPatch(typeof(InventoryUIController), nameof(InventoryUIController.RequestMoveItem))]
    [HarmonyPrefix]
    private static bool RequestMoveItem(InventoryUIController __instance, int source,
        InventorySectionController sourceController,
        int target, InventorySectionController targetController, int potentialEquipSlot,
        AItemContainer targetContainer)
    {
        if (sourceController == null || targetController == null) return true;

        bool sourceIsContainer = sourceController.section ==
            InventorySectionController.InventorySection.ItemContainer;
        bool targetIsContainer = targetController.section ==
            InventorySectionController.InventorySection.ItemContainer;
        if (!sourceIsContainer && !targetIsContainer) return true;

        AItemContainer active = Inventory.Instance?.ItemContainerRegistry?.ActiveContainer;
        if (active == null || targetContainer != null && targetContainer != active) return true;

        if (sourceIsContainer && targetIsContainer)
            return !ColdContainerUiIntegration.TryInterceptMove(active, source, target);

        if (sourceIsContainer)
        {
            int destinationSlot = targetController == __instance.handController
                ? Inventory.Instance?.GetFirstFreeSlot() ?? -1
                : __instance.GetAbsoluteSlotIndex(target,
                    targetController == __instance.hotbarController);
            return !ColdContainerUiIntegration.TryInterceptRemove(active, source,
                destinationSlot);
        }

        InventorySlotDisplayData data = sourceController.GetData(source);
        if (data?.Spec == null && sourceController.section ==
            InventorySectionController.InventorySection.Hand)
            data = sourceController.GetData(potentialEquipSlot);
        GameObject item = data?.Spec?.GetGameObject();
        return !ColdContainerUiIntegration.TryInterceptAdd(active, item, target);
    }

    [HarmonyPatch(typeof(InventoryUIController), nameof(InventoryUIController.RequestAddItem))]
    [HarmonyPrefix]
    private static bool RequestAddItem(InventorySlotDisplayData data, int slotIndex,
        InventorySectionController controller, ref int __result)
    {
        if (controller == null || controller.section !=
            InventorySectionController.InventorySection.ItemContainer)
            return true;
        AItemContainer active = Inventory.Instance?.ItemContainerRegistry?.ActiveContainer;
        if (!ColdContainerUiIntegration.TryInterceptAdd(active, data?.Spec?.GetGameObject(),
                slotIndex))
            return true;
        __result = -1;
        return false;
    }

    [HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.ToggleContainerAccess))]
    [HarmonyPostfix]
    private static void ToggleContainerAccess(ItemContainer __instance)
    {
        if (NetworkLifecycle.Instance?.IsClientRunning == true &&
            Inventory.Instance?.ItemContainerRegistry?.ActiveContainer == __instance)
            ColdContainerUiIntegration.Open(__instance);
    }

    [HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.AddItem))]
    [HarmonyPrefix]
    private static bool AddItem(ItemContainer __instance, GameObject item, int index,
        ref bool __result)
    {
        if (!ColdContainerUiIntegration.TryInterceptAdd(__instance, item, index)) return true;
        __result = false;
        return false;
    }

    [HarmonyPatch(typeof(ItemContainer), nameof(ItemContainer.RemoveItem), typeof(int),
        typeof(bool), typeof(bool))]
    [HarmonyPrefix]
    private static bool RemoveItem(ItemContainer __instance, int index, ref bool __result)
    {
        if (!ColdContainerUiIntegration.TryInterceptRemove(__instance, index)) return true;
        __result = false;
        return false;
    }

    [HarmonyPatch(typeof(AItemContainer), nameof(AItemContainer.MoveOrSwapItem))]
    [HarmonyPrefix]
    private static bool MoveOrSwapItem(AItemContainer __instance, int from, int to,
        ref bool __result)
    {
        if (!ColdContainerUiIntegration.TryInterceptMove(__instance, from, to)) return true;
        __result = false;
        return false;
    }
}
