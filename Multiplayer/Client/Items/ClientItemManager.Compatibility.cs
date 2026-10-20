using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV.InventorySystem;
using Multiplayer.Core.Items;
using Multiplayer.Core.Replication;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using UnityEngine;

namespace Multiplayer.Client.Items;

internal sealed partial class ClientItemManager
{
    // Dynamic projection creation, unbound local items and their temporary compatibility adoption.
    // Dynamic client projections are fresh instances owned by one host-authoritative logical
    // lifetime.
    private bool CreateItem(ItemUpdateData snapshot, bool acknowledge = true)
    {
        if (snapshot == null || snapshot.ItemNetId == 0)
        {
            Multiplayer.LogError($"NetworkedItemManager.CreateItem() Invalid snapshot! ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
            return false;
        }

        if (!shell.TryGetItemPrefab(snapshot.PrefabName, out GameObject prefab))
        {
            Multiplayer.LogError($"NetworkedItemManager.CreateItem() Unable to load prefab for ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
            return false;
        }

        GameObject gameObject = UnityEngine.Object.Instantiate(prefab,
            snapshot.ItemPosition + WorldMover.currentMove, snapshot.ItemRotation);
        NetworkedItem newItem = gameObject.GetOrAddComponent<NetworkedItem>();
        NetworkedItemManager.TraceItem("item.instantiated", newItem, new() { ["prefabName"] = snapshot.PrefabName });

        newItem.NetId = snapshot.ItemNetId;
        newItem.Client()?.SetNetworkBinding(true);
        newItem.gameObject.SetActive(true);
        NetworkedItemManager.TraceItem("item.created", newItem, DebugValueSnapshotter.SnapshotObject(snapshot));

        newItem.ReceiveSnapshot(snapshot);
        if (acknowledge)
            ItemClient.Current.SendWorldItemProjectionAck(snapshot.ItemNetId,
                snapshot.AuthorityRevision, true);
        return true;
    }

    /// <summary>The world has loaded: classify every item this game made before the host's arrive.</summary>
    internal void QuarantineUnboundClientItems()
    {
        shell.EnsureAuthoredCatalogue();

        // The host owns canonical existence. Preserve exact authored scene projections in a
        // dormant state, retain only the narrow temporary-adoption compatibility set, and
        // destroy every other unbound client object.
        foreach (var item in NetworkedItem.GetAll())
        {
            try
            {
                if (item == null || item.NetId != 0)
                    continue;

                shell.ScheduleTrackedValueFinalization(item);
                if (item.IsSceneAuthored)
                {
                    RetireClientProjection(item, "initial-authored-quarantine");
                }
                else if (IsTemporaryClientAdoptionAllowed(item, out _))
                {
                    item.Client()?.AllowUnboundLocalInteraction();
                }
                else
                {
                    RetireClientProjection(item, "initial-unbound-client-object");
                }
            }
            catch (Exception ex)
            {
                (NetworkLifecycle.Instance.LocalSession as NetworkManager)?.LogError($"Error quarantining unbound client item: {ex.Message}");
            }
        }

        ClientInitialised = true;
    }

    public void RegisterUnboundClientItem(NetworkedItem item)
    {
        if (item == null || item.NetId != 0 || DormantAuthoredClientProjections.Contains(item))
            return;
        shell.ScheduleTrackedValueFinalization(item);
        Dictionary<string, object> classification = new();
        bool allowInteraction = IsTemporaryClientAdoptionAllowed(item, out string compatibilityReason);
        if (allowInteraction)
            item.Client()?.AllowUnboundLocalInteraction();
        else
            item.Client()?.GateAsSceneObjectAwaitingHostCreate();
        classification["classification"] = allowInteraction ? "temporary-client-adoption-compatibility" :
            item.IsSceneAuthored ? "host-authored-projection" : "scene-authored-clutter";
        classification["compatibilityReason"] = compatibilityReason;
        NetworkedItemManager.TraceItem("item.unbound-classified", item, classification);
        if (PendingUnboundClientItemSet.Add(item))
            PendingUnboundClientItems.Enqueue(item);
    }

    private const float UnboundInventoryAdoptionDelaySeconds = 10f;

    /// <summary>
    /// Starting items a player's own game makes as it joins (DV's safeguard adds what the server says
    /// they don't own) stay unbound until first used, and until then only that game knows them: a
    /// crash lost them for good (a sandbox's boombox and handheld console, 2026-10-17). Once the
    /// server's originals have had time to arrive, ask it to adopt whatever is still unbound in the
    /// inventory. Where the server holds an original, it hands that back instead
    /// (TryAdoptClientItem), so this can't duplicate.
    /// </summary>
    internal void AdoptUnboundInventoryItemsSoon() => shell.StartCoroutine(AdoptUnboundInventoryItems());

    private IEnumerator AdoptUnboundInventoryItems()
    {
        yield return new WaitForSecondsRealtime(UnboundInventoryAdoptionDelaySeconds);
        if (NetworkLifecycle.Instance == null || NetworkLifecycle.Instance.IsHost() || !NetworkLifecycle.Instance.IsClientRunning)
            yield break;
        int requested = 0;
        foreach (NetworkedItem item in NetworkedItem.GetAll().Where(item => item != null && item.NetId == 0).ToArray())
        {
            bool inInventory;
            try { inInventory = Inventory.Instance?.Contains(item.gameObject, false) == true; }
            catch { inInventory = false; }
            if (!inInventory)
                continue;
            RequestItemAdoption(item, "unbound-in-inventory-after-join");
            requested++;
        }
        if (requested > 0)
            Multiplayer.Log($"Asked the server to adopt {requested} item(s) only this game knew, from the inventory");
    }

    public void RequestItemAdoption(NetworkedItem item, string reason)
    {
        if (item == null || item.NetId != 0)
            return;
        if (!IsTemporaryClientAdoptionAllowed(item, out string compatibilityReason))
        {
            DebugRuntime.Publish("item", "item.adoption-rejected", DebugRuntimeSide.Client,
                DebugSeverity.Error, "Item", "0", new()
                {
                    ["reason"] = "temporary-compatibility-policy-rejected",
                    ["compatibilityReason"] = compatibilityReason,
                    ["trigger"] = reason ?? string.Empty
                });
            RetireClientProjection(item, "temporary-adoption-policy-rejected");
            return;
        }
        string token = Guid.NewGuid().ToString("N");
        ItemAdoptionRequestData request = item.Client()?.CreateItemAdoptionRequest(token) ?? default;
        if (request.Snapshot == null || string.IsNullOrWhiteSpace(request.PrefabName))
        {
            DebugRuntime.Publish("item", "item.adoption-rejected", DebugRuntimeSide.Client, DebugSeverity.Error,
                "Item", "0", new() { ["reason"] = "local-snapshot-unavailable", ["trigger"] = reason ?? string.Empty });
            RetireClientProjection(item, "temporary-adoption-snapshot-unavailable");
            return;
        }
        AdoptionBeginStatus begin = TemporaryClientAdoptions.TryBegin(item, token);
        if (begin != AdoptionBeginStatus.Started)
        {
            DebugRuntime.Publish("item", "item.adoption-request-suppressed", DebugRuntimeSide.Client,
                DebugSeverity.Warning, "Item", "0", new()
                {
                    ["reason"] = begin.ToString(),
                    ["adoptionToken"] = token
                });
            return;
        }
        Dictionary<string, object> data = item.LocalStateDebugData();
        data["adoptionToken"] = token;
        data["trigger"] = reason ?? string.Empty;
        data["snapshot"] = DebugTrace.ItemSnapshotData(request.Snapshot);
        DebugRuntime.Publish("item", "item.adoption-requested", DebugRuntimeSide.Client, DebugSeverity.Info,
            "Item", "0", data);
        ItemClient.Current?.SendItemAdoption(request);
    }

    internal void ApplyItemAdoptionResult(ItemAdoptionResultData result)
    {
        string token = result.AdoptionToken ?? string.Empty;
        AdoptionResolution<NetworkedItem> resolution = TemporaryClientAdoptions.Resolve(token,
            result.Accepted, result.AssignedNetId);
        NetworkedItem item = resolution.Item;
        if (resolution.Status is AdoptionResolutionStatus.UnknownToken or
            AdoptionResolutionStatus.AlreadyCompleted)
        {
            DebugRuntime.Publish("item", "item.adoption-result-ignored", DebugRuntimeSide.Client,
                DebugSeverity.Warning, "Item", result.AssignedNetId.ToString(), new()
                {
                    ["adoptionToken"] = token,
                    ["reason"] = resolution.Status.ToString(),
                    ["assignedNetId"] = result.AssignedNetId
                });
            return;
        }
        if (item == null)
            return;

        Dictionary<string, object> data = item.LocalStateDebugData();
        data["adoptionToken"] = token;
        data["assignedNetId"] = result.AssignedNetId;
        data["rejectionReason"] = result.RejectionReason ?? string.Empty;
        if (resolution.Status != AdoptionResolutionStatus.Accepted)
        {
            DebugRuntime.Publish("item", "item.adoption-rejected", DebugRuntimeSide.Client, DebugSeverity.Warning,
                "Item", "0", new Dictionary<string, object>(data)
                {
                    ["coordinatorStatus"] = resolution.Status.ToString()
                });
            RetireClientProjection(item, "temporary-adoption-host-rejected");
            return;
        }

        item.Client()?.CompleteItemAdoption(result);
        shell.ScheduleTrackedValueFinalization(item);
        DebugRuntime.Publish("item", "item.adoption-complete", DebugRuntimeSide.Client, DebugSeverity.Info,
            "Item", result.AssignedNetId.ToString(), data);
    }

    /// <summary>
    /// Client: retire an unbound local copy (fresh starting item, NetId 0) of the item the host is
    /// placing into our inventory, preferring the copy in the original's reserved slot.
    /// </summary>
    /// <returns>The inventory slot the duplicate held, -1 if there was none.</returns>
    public int DiscardUnboundLocalDuplicate(NetworkedItem original)
    {
        string prefab = original?.Item?.InventorySpecs?.ItemPrefabName;
        Inventory inventory = Inventory.Instance;
        if (string.IsNullOrEmpty(prefab) || inventory == null)
            return -1;

        NetworkedItem duplicate = null;
        int duplicateSlot = -1;
        int claimSlot = original.InventoryClaimSlot;
        for (int slot = 0; slot < inventory.Capacity; slot++)
        {
            GameObject candidate = inventory.PeekItemAtSlot(slot, false);
            NetworkedItem candidateItem = candidate != null ? candidate.GetComponent<NetworkedItem>() : null;
            if (candidateItem == null || candidateItem == original || candidateItem.NetId != 0 ||
                candidateItem.Item?.InventorySpecs?.ItemPrefabName != prefab)
                continue;
            duplicate = candidateItem;
            duplicateSlot = slot;
            if (slot == claimSlot)
                break;
        }
        if (duplicate == null)
            return -1;

        Multiplayer.Log($"Replacing unbound local {prefab} with restored original {original.NetId}");
        RetireClientProjection(duplicate, "superseded-by-restored-original");
        return duplicateSlot;
    }

    public void ResolveAuthoritativeBindingCollision(NetworkedItem incoming, ushort authoritativeId, Type trackedItemType)
    {
        if (incoming == null || authoritativeId == 0)
            return;
        if (!NetworkedItem.TryGet(authoritativeId, out NetworkedItem existing) || existing == null || existing == incoming)
            return;

        DebugRuntime.Publish("item", "item.authoritative-binding-collision", DebugRuntimeSide.Client, DebugSeverity.Error,
            "Item", authoritativeId.ToString(), new()
            {
                ["existingInstanceId"] = existing.gameObject.GetInstanceID(),
                ["existingPath"] = existing.gameObject.GetPath(),
                ["existingTrackedItemType"] = existing.TrackedItemType?.FullName ?? string.Empty,
                ["incomingInstanceId"] = incoming.gameObject.GetInstanceID(),
                ["incomingPath"] = incoming.gameObject.GetPath(),
                ["incomingTrackedItemType"] = trackedItemType?.FullName ?? string.Empty
            });

        // The job/lifecycle-created object is the authoritative representation. Retire any
        // earlier generic copy before the new object takes ownership of the ID lookup.
        RetireClientProjection(existing, "authoritative-binding-collision");
    }

    private bool IsTemporaryClientAdoptionAllowed(NetworkedItem item, out string reason)
    {
        if (!TemporaryClientAdoptionCompatibility.AllowsLocalItem(item,
                shell.DoNotCreateItem, out reason))
            return false;
        if (IsPlayerOrStoredItem(item, out _))
            return true;
        reason = "not-player-or-storage-associated";
        return false;
    }

    private static bool IsPlayerOrStoredItem(NetworkedItem item, out Dictionary<string, object> data)
    {
        bool belongsToPlayer = item?.Item?.InventorySpecs?.BelongsToPlayer == true;
        bool grabbed = item?.Item?.IsGrabbed() == true;
        bool inventoryMember = false;
        bool itemContainerMember = false;
        bool worldStorageMember = false;
        bool storageInventoryMember = false;
        bool storageLostAndFoundMember = false;
        bool storageItemContainerMember = false;
        bool storageAvailable = StorageController.Instance != null;
        try
        {
            inventoryMember = Inventory.Instance?.Contains(item.gameObject, false) == true;
            var container = Inventory.Instance?.ItemContainerRegistry?.GetItemContainerAndIndex(item.gameObject);
            itemContainerMember = container.HasValue && container.Value.Item1 != null;
            StorageController storage = StorageController.Instance;
            worldStorageMember = storage?.StorageWorld?.ContainsItem(item.Item) == true;
            storageInventoryMember = storage?.StorageInventory?.ContainsItem(item.Item) == true;
            storageLostAndFoundMember = storage?.StorageLostAndFound?.ContainsItem(item.Item) == true;
            storageItemContainerMember = storage?.StorageItemContainers?.ContainsItem(item.Item) == true;
        }
        catch (Exception exception)
        {
            data = new Dictionary<string, object>
            {
                ["classificationError"] = exception.Message,
                ["storageAvailable"] = storageAvailable
            };
            // Unknown is safer than briefly disabling a legitimate grab. The reconciliation
            // queue will classify it again once the storage systems are available.
            return true;
        }

        data = new Dictionary<string, object>
        {
            ["belongsToPlayer"] = belongsToPlayer,
            ["grabbed"] = grabbed,
            ["inventoryMember"] = inventoryMember,
            ["itemContainerMember"] = itemContainerMember,
            ["worldStorageMember"] = worldStorageMember,
            ["storageInventoryMember"] = storageInventoryMember,
            ["storageLostAndFoundMember"] = storageLostAndFoundMember,
            ["storageItemContainerMember"] = storageItemContainerMember,
            ["storageAvailable"] = storageAvailable
        };
        return !storageAvailable || belongsToPlayer || grabbed || inventoryMember || itemContainerMember ||
            worldStorageMember || storageInventoryMember || storageLostAndFoundMember || storageItemContainerMember;
    }

    private void RetireClientProjection(NetworkedItem netItem, string reason)
    {
        if (netItem == null)
            return;

        bool authored = netItem.IsSceneAuthored;
        ushort retiredNetId = netItem.NetId;
        NetworkedItemManager.TraceItem(authored ? "item.authored-projection-dormant" : "item.dynamic-projection-destroyed",
            netItem, new()
            {
                ["prefabName"] = netItem.Item?.InventorySpecs?.itemPrefabName ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["retiredNetId"] = retiredNetId
            });
        netItem.Client()?.SetNetworkBinding(false);
        StorageIntegration.MoveTo(netItem.Item, StorageMembership.None);
        InventoryIntegration.RevokeMembership(netItem.gameObject, retiredNetId);
        InventoryIntegration.PurgeContainerMembership(netItem.gameObject);
        netItem.gameObject.SetActive(false);
        netItem.NetId = 0;
        if (authored)
        {
            netItem.Client()?.ResetNetworkLifetime();
            DormantAuthoredClientProjections.Add(netItem);
            return;
        }

        DormantAuthoredClientProjections.Remove(netItem);
        UnityEngine.Object.Destroy(netItem.gameObject);
    }
}
