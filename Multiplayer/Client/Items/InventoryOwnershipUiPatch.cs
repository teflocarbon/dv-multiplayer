using DV.UI.Inventory;
using HarmonyLib;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Core.Items;
using Multiplayer.Debugging.Protocol;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.Client.Items;

[HarmonyPatch]
internal static class InventoryOwnershipUiPatch
{
    private static readonly HashSet<string> markedEvents = new();

    [HarmonyPatch(typeof(InventoryUIController), "OnGetClicked")]
    [HarmonyPrefix]
    private static bool BeforeGetClicked(InventoryUIController __instance, int slotIndex,
        InventorySectionController controller) =>
        HandleGetClicked(__instance, slotIndex, controller);

    private static uint lostItemRequestId;

    /// <returns>True when the original Derail Valley Get handler should execute.</returns>
    private static bool HandleGetClicked(InventoryUIController controller, int relativeSlot,
        InventorySectionController section)
    {
        global::Multiplayer.Multiplayer.Log($"[Inventory Integration] Get clicked: " +
            $"relativeSlot={relativeSlot}, section={section?.section.ToString() ?? "null"}, " +
            $"runtimeActive={InventoryIntegration.RuntimeActive}");
        if (!InventoryIntegration.RuntimeActive ||
            !InventoryIntegration.TrySnapshotSlot(controller, relativeSlot, section, out InventorySlotRuntimeSnapshot slot))
            return true;

        NetworkedItem item = slot.NetworkedItem;
        LostItemData lost = item == null ? null : LostAndFoundList.Items
            .FirstOrDefault(entry => entry?.NetId == item.NetId);
        InventoryItemPresentation presentation = InventoryIntegration.Project(item, lost != null);

        global::Multiplayer.Multiplayer.Log($"[Inventory Integration] Slot snapshot: " +
            $"absoluteSlot={slot.AbsoluteSlot}, dropped={slot.Dropped}, reserved={slot.Reserved}, " +
            $"item={slot.ItemObject?.name ?? "null"}, netId={item?.NetId ?? 0}, " +
            $"owner=P{item?.PersistentOwnerPlayerId ?? 0}, local=P{InventoryIntegration.LocalPlayerId}, " +
            $"route={presentation.RecallRoute}");

        if (presentation.RecallRoute == InventoryRecallRoute.Vanilla)
            return true;
        if (presentation.RecallRoute == InventoryRecallRoute.BlockForeignOwner)
        {
            DebugRuntime.Publish("item", "item.foreign-item-recall-blocked",
                DebugRuntimeSide.Client, DebugSeverity.Warning, "Item", item.NetId.ToString(), new()
                {
                    ["localPlayerId"] = InventoryIntegration.LocalPlayerId,
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
                    ["localPlayerId"] = InventoryIntegration.LocalPlayerId,
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
            ItemClient.Current?.RequestLostItemRetrieval(requestId, lost.Handle,
                item.NetId, lost.Revision, slot.AbsoluteSlot);
            return false;
        }

        NetworkedItemManager.Instance?.RequestItemRecall(item, slot.AbsoluteSlot);
        return false;
    }

    [HarmonyPatch(typeof(InventorySlotVisualController), "UpdateVisuals")]
    [HarmonyPostfix]
    private static void AfterUpdateVisuals(InventorySlotVisualController __instance,
        InventorySlotDisplayData data)
    {
        if (__instance?.itemImage == null)
            return;
        Component spec = data?.Spec as Component;
        NetworkedItem item = spec != null
            ? spec.GetComponent<NetworkedItem>() ?? spec.GetComponentInParent<NetworkedItem>()
            : null;
        ColdContainerItemProxy coldProxy = data?.Spec as ColdContainerItemProxy;
        InventoryItemPresentation presentation = InventoryIntegration.Project(item,
            item != null && ItemServer.Current?.IsInLostAndFound(item.NetId) == true);
        ForeignItemInventoryMarker marker = __instance.itemImage.gameObject.GetComponent<ForeignItemInventoryMarker>() ??
            __instance.itemImage.gameObject.AddComponent<ForeignItemInventoryMarker>();
        bool foreign = coldProxy?.ForeignOwned == true || presentation.ShowForeignStyle;
        marker.SetForeign(foreign);
        if (!foreign)
            return;

        __instance.getButton?.gameObject.SetActive(false);
        byte localPlayerId = InventoryIntegration.LocalPlayerId;
        string key = coldProxy != null
            ? $"cold:{coldProxy.ContainerHandle}:{coldProxy.ItemHandle}:{localPlayerId}"
            : $"{item.NetId}:{item.PersistentOwnerPlayerId}:{localPlayerId}";
        if (markedEvents.Add(key))
            DebugRuntime.Publish("item", "item.foreign-item-ui-marked", DebugRuntimeSide.Client,
                entityType: coldProxy != null ? "Container" : "Item",
                entityId: coldProxy != null ? coldProxy.ContainerHandle.ToString() : item.NetId.ToString(), data: new()
                {
                    ["localPlayerId"] = localPlayerId,
                    ["persistentOwnerPlayerId"] = item?.PersistentOwnerPlayerId ?? 0,
                    ["coldItemHandle"] = coldProxy?.ItemHandle ?? 0,
                    ["color"] = "red"
                });
    }
}

internal sealed class ForeignItemInventoryMarker : MonoBehaviour
{
    private Image image;
    private Outline outline;
    private Color originalColor;
    private bool initialized;

    public void SetForeign(bool foreign)
    {
        if (!initialized)
        {
            image = GetComponent<Image>();
            originalColor = image != null ? image.color : Color.white;
            outline = gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0.95f, 0.08f, 0.08f, 1f);
            outline.effectDistance = new Vector2(3f, -3f);
            outline.useGraphicAlpha = true;
            initialized = true;
        }
        if (image != null)
            image.color = foreign ? new Color(1f, 0.28f, 0.28f, originalColor.a) : originalColor;
        if (outline != null)
            outline.enabled = foreign;
    }
}
