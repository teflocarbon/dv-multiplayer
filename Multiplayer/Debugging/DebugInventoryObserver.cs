using DV;
using DV.CabControls;
using DV.InventorySystem;
using DV.Items;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Debugging;

/// <summary>
/// Event-driven observation of Derail Valley's real inventory, storage and item-container
/// lifecycles. This is debug-only and never changes inventory contents or application flow.
/// </summary>
internal sealed class DebugInventoryObserver : MonoBehaviour
{
    private Inventory inventory;
    private StorageController storageController;
    private ItemContainerRegistry containerRegistry;
    private readonly Dictionary<StorageBase, Action<ItemBase>> storageAdded = new();
    private readonly Dictionary<StorageBase, Action<ItemBase>> storageRemoved = new();
    private readonly HashSet<AItemContainer> containers = new();

    private void Update()
    {
        if (!DebugRuntime.Enabled)
            return;
        if (inventory == null && Inventory.Instance != null)
            SubscribeInventory(Inventory.Instance);
        if (storageController == null && StorageController.Instance != null)
            SubscribeStorage(StorageController.Instance);
        if (containerRegistry == null && Inventory.Instance?.ItemContainerRegistry != null)
            SubscribeRegistry(Inventory.Instance.ItemContainerRegistry);
    }

    private void OnDestroy()
    {
        if (inventory != null)
            inventory.InventoryStatusChanged -= OnInventoryStatusChanged;
        if (containerRegistry != null)
            containerRegistry.RegistryUpdated -= OnContainerRegistryUpdated;
        foreach (AItemContainer container in new List<AItemContainer>(containers))
            UnsubscribeContainer(container);
        foreach (KeyValuePair<StorageBase, Action<ItemBase>> pair in storageAdded)
            if (pair.Key != null) pair.Key.ItemAdded -= pair.Value;
        foreach (KeyValuePair<StorageBase, Action<ItemBase>> pair in storageRemoved)
            if (pair.Key != null) pair.Key.ItemRemoved -= pair.Value;
        storageAdded.Clear();
        storageRemoved.Clear();
    }

    private void SubscribeInventory(Inventory value)
    {
        inventory = value;
        inventory.InventoryStatusChanged += OnInventoryStatusChanged;
        DebugRuntime.Publish("inventory", "inventory.observer-attached", Side(), data: new()
        {
            ["capacity"] = inventory.Capacity,
            ["handCapacity"] = inventory.HandCapacity
        });
    }

    private void SubscribeStorage(StorageController value)
    {
        storageController = value;
        SubscribeStorage(value.StorageWorld, "World");
        SubscribeStorage(value.StorageInventory, "Inventory");
        SubscribeStorage(value.StorageLostAndFound, "LostAndFound");
        SubscribeStorage(value.StorageInstalledGadgets, "InstalledGadgets");
        SubscribeStorage(value.StorageItemContainers, "ItemContainers");
    }

    private void SubscribeStorage(StorageBase storage, string storageName)
    {
        if (storage == null || storageAdded.ContainsKey(storage))
            return;
        Action<ItemBase> added = item => PublishStorageTransition("storage.item-added", storage, storageName, item);
        Action<ItemBase> removed = item => PublishStorageTransition("storage.item-removed", storage, storageName, item);
        storageAdded[storage] = added;
        storageRemoved[storage] = removed;
        storage.ItemAdded += added;
        storage.ItemRemoved += removed;
    }

    private void SubscribeRegistry(ItemContainerRegistry value)
    {
        containerRegistry = value;
        containerRegistry.RegistryUpdated += OnContainerRegistryUpdated;
        foreach (AItemContainer container in containerRegistry.GetAllItemContainers())
            SubscribeContainer(container);
    }

    private void OnInventoryStatusChanged(InventorySlotState primary, InventoryActionType primaryAction,
        InventorySlotState secondary, InventoryActionType secondaryAction)
    {
        if (!DebugRuntime.EnabledFor("inventory"))
            return;

        Dictionary<string, object> envelope = new()
        {
            ["primaryAction"] = primaryAction.ToString(),
            ["primaryActionValue"] = (int)primaryAction,
            ["primary"] = SnapshotSlot(primary),
            ["secondaryAction"] = secondaryAction.ToString(),
            ["secondaryActionValue"] = (int)secondaryAction,
            ["secondary"] = SnapshotSlot(secondary),
            ["inventoryItemCount"] = inventory?.ItemCount ?? 0,
            ["inventoryItemCountIncludingDropped"] = inventory?.ItemCountIncludingDropped ?? 0
        };
        DebugRuntime.Publish("inventory", "inventory.transition-observed", Side(), data: envelope);

        HashSet<GameObject> emitted = new();
        PublishItemTransition(primary, primaryAction, "primary", emitted);
        PublishItemTransition(secondary, secondaryAction, "secondary", emitted);
    }

    private void PublishItemTransition(InventorySlotState state, InventoryActionType action,
        string eventSide, HashSet<GameObject> emitted)
    {
        if (state.item == null || !emitted.Add(state.item))
            return;
        Dictionary<string, object> data = SnapshotSlot(state);
        data["action"] = action.ToString();
        data["actionValue"] = (int)action;
        data["eventSide"] = eventSide;
        AddLivePlacement(data, state.item);
        PublishForItem("inventory.item-transition", state.item, data);
    }

    private void PublishStorageTransition(string eventName, StorageBase storage, string storageName, ItemBase item)
    {
        if (!DebugRuntime.EnabledFor("inventory") || item == null)
            return;
        Dictionary<string, object> data = new()
        {
            ["storage"] = storageName,
            ["storageType"] = storage?.storageType.ToString() ?? string.Empty,
            ["storageCount"] = storage?.GetStorageItemList()?.Count ?? 0,
            ["acceptsNonEssential"] = storage?.acceptsNonEssential ?? false,
            ["belongsToPlayer"] = item.InventorySpecs?.BelongsToPlayer ?? false,
            ["isEssential"] = item.InventorySpecs?.IsEssential ?? false
        };
        AddLivePlacement(data, item.gameObject);
        PublishForItem(eventName, item.gameObject, data);
    }

    private void OnContainerRegistryUpdated(AItemContainer container, bool added)
    {
        if (added) SubscribeContainer(container); else UnsubscribeContainer(container);
        if (!DebugRuntime.EnabledFor("inventory") || container == null)
            return;
        DebugRuntime.Publish("inventory", added ? "container.registered" : "container.unregistered", Side(),
            entityType: "Container", entityId: container.ContainerId ?? string.Empty, data: SnapshotContainer(container));
    }

    private void SubscribeContainer(AItemContainer container)
    {
        if (container == null || !containers.Add(container))
            return;
        container.ItemContainerDataChanged += OnContainerDataChanged;
        container.ItemDropped += OnContainerItemDropped;
        container.ItemContainerNestedInChanged += OnContainerNestedChanged;
    }

    private void UnsubscribeContainer(AItemContainer container)
    {
        if (container == null || !containers.Remove(container))
            return;
        container.ItemContainerDataChanged -= OnContainerDataChanged;
        container.ItemDropped -= OnContainerItemDropped;
        container.ItemContainerNestedInChanged -= OnContainerNestedChanged;
    }

    private void OnContainerDataChanged(AItemContainer container, int sourceIndex, int destinationIndex)
    {
        if (!DebugRuntime.EnabledFor("inventory") || container == null)
            return;
        Dictionary<string, object> data = SnapshotContainer(container);
        data["sourceIndex"] = sourceIndex;
        data["destinationIndex"] = destinationIndex;
        data["sourceItem"] = SnapshotContainerItem(container, sourceIndex);
        data["destinationItem"] = SnapshotContainerItem(container, destinationIndex);
        DebugRuntime.Publish("inventory", "container.contents-changed", Side(),
            entityType: "Container", entityId: container.ContainerId ?? string.Empty, data: data);
    }

    private void OnContainerItemDropped(GameObject item)
    {
        if (!DebugRuntime.EnabledFor("inventory") || item == null)
            return;
        Dictionary<string, object> data = new();
        AddLivePlacement(data, item);
        PublishForItem("container.item-dropped", item, data);
    }

    private void OnContainerNestedChanged(AItemContainer container,
        (AItemContainer oldFirstNest, AItemContainer oldLastNest) oldNested)
    {
        if (!DebugRuntime.EnabledFor("inventory") || container == null)
            return;
        Dictionary<string, object> data = SnapshotContainer(container);
        data["oldFirstContainerId"] = oldNested.oldFirstNest?.ContainerId ?? string.Empty;
        data["oldLastContainerId"] = oldNested.oldLastNest?.ContainerId ?? string.Empty;
        data["newFirstContainerId"] = container.NestedIn.firstNest?.ContainerId ?? string.Empty;
        data["newLastContainerId"] = container.NestedIn.lastNest?.ContainerId ?? string.Empty;
        DebugRuntime.Publish("inventory", "container.nesting-changed", Side(),
            entityType: "Container", entityId: container.ContainerId ?? string.Empty, data: data);
    }

    private static Dictionary<string, object> SnapshotSlot(InventorySlotState state)
    {
        return new Dictionary<string, object>
        {
            ["slotIndex"] = state.slotIndex,
            ["itemState"] = state.itemState.ToString(),
            ["isLocked"] = state.isLocked,
            ["isReserved"] = state.isReserved,
            ["equipSlot"] = state.equipSlot,
            ["item"] = SnapshotObjectIdentity(state.item)
        };
    }

    private static Dictionary<string, object> SnapshotContainer(AItemContainer container)
    {
        return new Dictionary<string, object>
        {
            ["containerId"] = container?.ContainerId ?? string.Empty,
            ["containerType"] = container?.GetType().FullName ?? string.Empty,
            ["capacity"] = container?.Capacity ?? 0,
            ["itemCount"] = container?.ItemCount ?? 0,
            ["directInteractionAllowed"] = container?.DirectInteractionAllowed ?? false,
            ["firstNestedIn"] = container?.NestedIn.firstNest?.ContainerId ?? string.Empty,
            ["lastNestedIn"] = container?.NestedIn.lastNest?.ContainerId ?? string.Empty
        };
    }

    private static object SnapshotContainerItem(AItemContainer container, int index)
    {
        return container != null && index >= 0 && index < container.Capacity
            ? SnapshotObjectIdentity(container[index])
            : null;
    }

    private static Dictionary<string, object> SnapshotObjectIdentity(GameObject item)
    {
        if (item == null)
            return null;
        ItemBase itemBase = item.GetComponent<ItemBase>();
        NetworkedItem networked = null;
        if (itemBase != null)
            NetworkedItem.TryGetNetworkedItem(itemBase, out networked);
        InventoryItemSpec spec = item.GetComponent<InventoryItemSpec>();
        return new Dictionary<string, object>
        {
            ["netId"] = networked?.NetId ?? 0,
            ["unityInstanceId"] = item.GetInstanceID(),
            ["name"] = item.name ?? string.Empty,
            ["prefab"] = spec?.ItemPrefabName ?? string.Empty,
            ["belongsToPlayer"] = spec?.BelongsToPlayer ?? false,
            ["isEssential"] = spec?.IsEssential ?? false
        };
    }

    private static void AddLivePlacement(Dictionary<string, object> data, GameObject item)
    {
        if (item == null)
            return;
        ItemBase itemBase = item.GetComponent<ItemBase>();
        data["activeSelf"] = item.activeSelf;
        data["activeInHierarchy"] = item.activeInHierarchy;
        data["layer"] = item.layer;
        data["position"] = DebugValueSnapshotter.Snapshot(item.transform.position);
        data["parent"] = item.transform.parent?.name ?? string.Empty;
        try
        {
            Inventory inv = Inventory.Instance;
            StorageController storage = StorageController.Instance;
            var container = inv?.ItemContainerRegistry?.GetItemContainerIdAndIndex(item) ?? (null, -1);
            data["inventorySlot"] = inv?.IndexOf(item) ?? -1;
            data["equippedSlot"] = inv?.GetEquipSlotForItem(item) ?? -1;
            data["containerId"] = container.Item1 ?? string.Empty;
            data["containerSlot"] = container.Item2;
            data["storageWorld"] = storage?.StorageWorld?.ContainsItem(itemBase) ?? false;
            data["storageInventory"] = storage?.StorageInventory?.ContainsItem(itemBase) ?? false;
            data["storageLostAndFound"] = storage?.StorageLostAndFound?.ContainsItem(itemBase) ?? false;
            data["storageItemContainers"] = storage?.StorageItemContainers?.ContainsItem(itemBase) ?? false;
            data["multipleStorages"] = storage?.ItemInMultipleStorages(itemBase) ?? false;
        }
        catch (Exception exception)
        {
            data["placementSnapshotError"] = exception.Message;
        }
    }

    private static void PublishForItem(string eventName, GameObject item, Dictionary<string, object> data)
    {
        ItemBase itemBase = item?.GetComponent<ItemBase>();
        NetworkedItem networked = null;
        if (itemBase != null)
            NetworkedItem.TryGetNetworkedItem(itemBase, out networked);
        string entityId = networked != null && networked.NetId != 0
            ? networked.NetId.ToString()
            : $"unity:{item?.GetInstanceID() ?? 0}";
        DebugRuntime.Publish("inventory", eventName, Side(), entityType: "Item", entityId: entityId, data: data);
    }

    private static DebugRuntimeSide Side() => NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.IsHost()
        ? DebugRuntimeSide.Server
        : DebugRuntimeSide.Client;
}
