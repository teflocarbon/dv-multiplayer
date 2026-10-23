using DV.UI.Inventory;
using HarmonyLib;
using Multiplayer.Client.Items.Containers;
using Multiplayer.Client.Items.LostAndFound;
using Multiplayer.Core.Items.Inventory;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Debugging.Protocol;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.Client.Items.Inventory;

[HarmonyPatch]
internal static class InventoryOwnershipUiPatch
{
    private static readonly HashSet<string> markedEvents = new();

    [HarmonyPatch(typeof(InventoryUIController), "OnGetClicked")]
    [HarmonyPrefix]
    private static bool BeforeGetClicked(InventoryUIController __instance, int slotIndex,
        InventorySectionController controller) =>
        HandleGetClicked(__instance, slotIndex, controller);


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
        InventoryItemPresentation presentation = InventoryIntegration.Project(item,
            item != null && ItemServer.Current?.IsInLostAndFound(item.NetId) == true);

        global::Multiplayer.Multiplayer.Log($"[Inventory Integration] Slot snapshot: " +
            $"absoluteSlot={slot.AbsoluteSlot}, dropped={slot.Dropped}, reserved={slot.Reserved}, " +
            $"item={slot.ItemObject?.name ?? "null"}, netId={item?.NetId ?? 0}, " +
            $"relation={item?.Relation.ToString() ?? "none"}, local=P{InventoryIntegration.LocalPlayerId}, " +
            $"route={presentation.RecallRoute}");

        if (presentation.RecallRoute == InventoryRecallRoute.Vanilla)
            return true;
        if (presentation.RecallRoute == InventoryRecallRoute.BlockForeignOwner)
        {
            DebugRuntime.Publish("item", "item.foreign-item-recall-blocked",
                DebugRuntimeSide.Client, DebugSeverity.Warning, "Item", item.NetId.ToString(), new()
                {
                    ["localPlayerId"] = InventoryIntegration.LocalPlayerId,
                    ["relation"] = item.Relation.ToString(),
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
                    ["relation"] = item.Relation.ToString(),
                    ["isEssential"] = item.Item?.InventorySpecs?.IsEssential == true,
                    ["slot"] = slot.AbsoluteSlot
                });
            return false;
        }

        // A recall, from wherever it is: the server collects it from Lost and Found if it's there.
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
            : $"{item.NetId}:{item.Relation}:{localPlayerId}";
        if (markedEvents.Add(key))
            DebugRuntime.Publish("item", "item.foreign-item-ui-marked", DebugRuntimeSide.Client,
                entityType: coldProxy != null ? "Container" : "Item",
                entityId: coldProxy != null ? coldProxy.ContainerHandle.ToString() : item.NetId.ToString(), data: new()
                {
                    ["localPlayerId"] = localPlayerId,
                    ["relation"] = item?.Relation.ToString() ?? string.Empty,
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
