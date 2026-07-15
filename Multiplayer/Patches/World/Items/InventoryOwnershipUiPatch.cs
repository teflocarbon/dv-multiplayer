using DV.UI.Inventory;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data.Items;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.Patches.World.Items;

[HarmonyPatch]
internal static class InventoryOwnershipUiPatch
{
    private static readonly HashSet<string> markedEvents = new();
    private static uint lostItemRequestId;

    [HarmonyPatch(typeof(InventoryUIController), "OnGetClicked")]
    [HarmonyPrefix]
    private static bool BeforeGetClicked(InventoryUIController __instance, int slotIndex,
        InventorySectionController controller)
    {
        global::Multiplayer.Multiplayer.Log($"[LostAndFound Recall] Star clicked: relativeSlot={slotIndex}, " +
            $"controller={controller?.section.ToString() ?? "null"}, uiPresent={__instance != null}, " +
            $"lifecyclePresent={NetworkLifecycle.Instance != null}");
        if (__instance?.provider?.Inventory == null || NetworkLifecycle.Instance == null ||
            (!NetworkLifecycle.Instance.IsClientRunning && !NetworkLifecycle.Instance.IsHost()))
        {
            global::Multiplayer.Multiplayer.LogWarning("[LostAndFound Recall] Falling through to vanilla: " +
                $"inventoryPresent={__instance?.provider?.Inventory != null}, " +
                $"clientRunning={NetworkLifecycle.Instance?.IsClientRunning ?? false}, " +
                $"isHost={NetworkLifecycle.Instance?.IsHost() ?? false}");
            return true;
        }

        int absoluteSlot = __instance.GetAbsoluteSlotIndex(slotIndex, controller == __instance.hotbarController);
        GameObject itemObject = __instance.provider.Inventory.PeekItemAtSlot(absoluteSlot, true);
        NetworkedItem item = itemObject != null ? itemObject.GetComponent<NetworkedItem>() : null;
        global::Multiplayer.Multiplayer.Log($"[LostAndFound Recall] Slot resolved: absoluteSlot={absoluteSlot}, " +
            $"dropped={__instance.provider.Inventory.GetSlotDroppedState(absoluteSlot)}, " +
            $"reserved={__instance.provider.Inventory.GetSlotReservedState(absoluteSlot)}, " +
            $"item={itemObject?.name ?? "null"}, netId={item?.NetId ?? 0}, " +
            $"owner=P{item?.PersistentOwnerPlayerId ?? 0}, revision={item?.AuthorityRevision ?? 0}");
        if (itemObject == null || item == null || item.NetId == 0 || item.PersistentOwnerPlayerId == 0)
        {
            global::Multiplayer.Multiplayer.LogWarning("[LostAndFound Recall] Falling through to vanilla: " +
                $"itemPresent={itemObject != null}, networkedItemPresent={item != null}, " +
                $"netId={item?.NetId ?? 0}, owner=P{item?.PersistentOwnerPlayerId ?? 0}");
            return true;
        }

        byte localPlayerId = NetworkLifecycle.Instance.IsHost()
            ? NetworkLifecycle.Instance.Server?.SelfId ?? 0
            : NetworkLifecycle.Instance.Client?.PlayerId ?? 0;
        if (item.PersistentOwnerPlayerId != localPlayerId)
        {
            global::Multiplayer.Multiplayer.LogWarning($"[LostAndFound Recall] Blocked foreign recall: " +
                $"netId={item.NetId}, local=P{localPlayerId}, owner=P{item.PersistentOwnerPlayerId}");
            DebugRuntime.Publish("item", "item.foreign-item-recall-blocked", DebugRuntimeSide.Client,
                DebugSeverity.Warning, "Item", item.NetId.ToString(), new()
                {
                    ["localPlayerId"] = localPlayerId,
                    ["persistentOwnerPlayerId"] = item.PersistentOwnerPlayerId,
                    ["slot"] = absoluteSlot
                });
            return false;
        }

        // A virtually stored item must use the same retrieval operation as the Career Manager
        // screen. The ordinary recall packet is for a world/remote-hand item and does not carry
        // the inventory projection needed to revive an existing dropped silhouette.
        LostItemData lost = NetworkedLostAndFoundManager.ClientItems
            .FirstOrDefault(entry => entry?.NetId == item.NetId);
        global::Multiplayer.Multiplayer.Log($"[LostAndFound Recall] Routing: netId={item.NetId}, " +
            $"runtime={(NetworkLifecycle.Instance.IsHost() ? "host" : "client")}, " +
            $"clientListMatch={lost != null}, clientListCount={NetworkedLostAndFoundManager.ClientItems.Count}, " +
            $"localRevision={item.AuthorityRevision}, listedRevision={lost?.Revision ?? 0}");
        if (!NetworkLifecycle.Instance.IsHost() && lost != null)
        {
            uint requestId = unchecked(++lostItemRequestId);
            if (NetworkLifecycle.Instance.Client == null)
            {
                global::Multiplayer.Multiplayer.LogError($"[LostAndFound Recall] Cannot send retrieval: " +
                    $"client is null, request={requestId}, netId={item.NetId}");
            }
            else
            {
                global::Multiplayer.Multiplayer.Log($"[LostAndFound Recall] Sending Lost and Found retrieval: " +
                    $"request={requestId}, handle={lost.Handle}, netId={item.NetId}, " +
                    $"revision={lost.Revision}, slot={absoluteSlot}");
                NetworkLifecycle.Instance.Client.RequestLostItemRetrieval(
                    requestId, lost.Handle, item.NetId, lost.Revision, absoluteSlot);
            }
        }
        else
        {
            bool requested = NetworkedItemManager.Instance?.RequestItemRecall(item, absoluteSlot) ?? false;
            global::Multiplayer.Multiplayer.Log($"[LostAndFound Recall] Recall route returned: " +
                $"netId={item.NetId}, requested={requested}, managerPresent={NetworkedItemManager.Instance != null}");
        }
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
        ForeignItemInventoryMarker marker = __instance.itemImage.gameObject.GetComponent<ForeignItemInventoryMarker>() ??
            __instance.itemImage.gameObject.AddComponent<ForeignItemInventoryMarker>();
        bool foreign = item != null && item.NetId != 0 && item.IsForeignOwned;
        marker.SetForeign(foreign);
        if (!foreign)
            return;

        __instance.getButton?.gameObject.SetActive(false);
        byte localPlayerId = NetworkLifecycle.Instance.IsHost()
            ? NetworkLifecycle.Instance.Server?.SelfId ?? 0
            : NetworkLifecycle.Instance.Client?.PlayerId ?? 0;
        string key = $"{item.NetId}:{item.PersistentOwnerPlayerId}:{localPlayerId}";
        if (markedEvents.Add(key))
            DebugRuntime.Publish("item", "item.foreign-item-ui-marked", DebugRuntimeSide.Client,
                entityType: "Item", entityId: item.NetId.ToString(), data: new()
                {
                    ["localPlayerId"] = localPlayerId,
                    ["persistentOwnerPlayerId"] = item.PersistentOwnerPlayerId,
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
