using System.Collections.Generic;
using System.Linq;
using DV.Utils;
using UnityEngine;
using JetBrains.Annotations;
using Multiplayer.Networking.Data;
using Multiplayer.Components.Networking.World;
using System;
using Multiplayer.Utils;
using DV;
using DV.InventorySystem;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Items;
using Multiplayer.Core.Replication;
using DV.Booklets;
using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Networking.Data.Jobs;
using Multiplayer.Components.Networking.World.Containers;
using Multiplayer.Integrations.Inventory;
using Multiplayer.Integrations.Storage;
using Multiplayer.Components.Networking.World.WorldItems;
using Multiplayer.Networking.Packets.Clientbound;

namespace Multiplayer.Components.Networking.World;

public partial class NetworkedItemManager
{
    // Prefab lookup, dynamic projection creation, temporary compatibility adoption, and recall
    // requests. Dynamic client projections are fresh instances owned by one host-authoritative
    // logical lifetime.
    private bool CreateItem(ItemUpdateData snapshot, bool acknowledge = true)
    {
        if (snapshot == null || snapshot.ItemNetId == 0)
        {
            Multiplayer.LogError($"NetworkedItemManager.CreateItem() Invalid snapshot! ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
            return false;
        }

        if (!ItemPrefabs.TryGetValue(snapshot.PrefabName, out InventoryItemSpec spec))
        {
            Multiplayer.LogError($"NetworkedItemManager.CreateItem() Unable to load prefab for ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
            return false;
        }

        GameObject gameObject = Instantiate(spec.gameObject,
            snapshot.ItemPosition + WorldMover.currentMove, snapshot.ItemRotation);
        NetworkedItem newItem = gameObject.GetOrAddComponent<NetworkedItem>();
        TraceItem("item.instantiated", newItem, new() { ["prefabName"] = snapshot.PrefabName });

        newItem.NetId = snapshot.ItemNetId;
        newItem.SetClientNetworkBinding(true);
        newItem.gameObject.SetActive(true);
        TraceItem("item.created", newItem, DebugValueSnapshotter.SnapshotObject(snapshot));

        newItem.ReceiveSnapshot(snapshot);
        if (acknowledge)
            NetworkLifecycle.Instance.Client.SendWorldItemProjectionAck(snapshot.ItemNetId,
                snapshot.AuthorityRevision, true);
        return true;
    }

    private void BuildPrefabLookup()
    {
        NetworkLifecycle.Instance.Client.LogDebug(() => $"BuildPrefabLookup()");

        foreach (var item in Globals.G.Items.items)
        {
            if (!ItemPrefabs.ContainsKey(item.ItemPrefabName))
            {
                ItemPrefabs[item.itemPrefabName] = item;
            }
        }
    }

    internal bool TryGetItemPrefab(string prefabName, out GameObject prefab)
    {
        prefab = null;
        if (string.IsNullOrWhiteSpace(prefabName) ||
            !ItemPrefabs.TryGetValue(prefabName, out InventoryItemSpec spec) || spec?.gameObject == null)
            return false;
        prefab = spec.gameObject;
        return true;
    }

    public void QuarantineUnboundClientItems()
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        EnsureAuthoredCatalogue();

        // The host owns canonical existence. Preserve exact authored scene projections in a
        // dormant state, retain only the narrow temporary-adoption compatibility set, and
        // destroy every other unbound client object.
        foreach (var item in NetworkedItem.GetAll())
        {
            try
            {
                if (item == null || item.NetId != 0)
                    continue;

                ScheduleTrackedValueFinalization(item);
                if (item.IsSceneAuthored)
                {
                    RetireClientProjection(item, "initial-authored-quarantine");
                }
                else if (IsTemporaryClientAdoptionAllowed(item, out _))
                {
                    item.AllowUnboundLocalInteraction();
                }
                else
                {
                    RetireClientProjection(item, "initial-unbound-client-object");
                }
            }
            catch (Exception ex)
            {
                NetworkLifecycle.Instance.Client.LogError($"Error quarantining unbound client item: {ex.Message}");
            }
        }

        ClientInitialised = true;
    }

    internal void RegisterUnboundClientItem(NetworkedItem item)
    {
        if (item == null || item.NetId != 0 || DormantAuthoredClientProjections.Contains(item))
            return;
        ScheduleTrackedValueFinalization(item);
        Dictionary<string, object> classification = new();
        bool allowInteraction = IsTemporaryClientAdoptionAllowed(item, out string compatibilityReason);
        if (allowInteraction)
            item.AllowUnboundLocalInteraction();
        else
            item.GateAsSceneObjectAwaitingHostCreate();
        classification["classification"] = allowInteraction ? "temporary-client-adoption-compatibility" :
            item.IsSceneAuthored ? "host-authored-projection" : "scene-authored-clutter";
        classification["compatibilityReason"] = compatibilityReason;
        TraceItem("item.unbound-classified", item, classification);
        if (PendingUnboundClientItemSet.Add(item))
            PendingUnboundClientItems.Enqueue(item);
    }

    internal void RequestItemAdoption(NetworkedItem item, string reason)
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
        ItemAdoptionRequestData request = item.CreateItemAdoptionRequest(token);
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
        NetworkLifecycle.Instance.Client?.SendItemAdoption(request);
    }

    public void ApplyItemAdoptionResult(ItemAdoptionResultData result)
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

        item.CompleteItemAdoption(result);
        ScheduleTrackedValueFinalization(item);
        DebugRuntime.Publish("item", "item.adoption-complete", DebugRuntimeSide.Client, DebugSeverity.Info,
            "Item", result.AssignedNetId.ToString(), data);
    }

    public bool RequestItemRecall(NetworkedItem item, int requestedSlot)
    {
        if (item == null || item.NetId == 0)
            return false;
        byte localPlayerId = NetworkLifecycle.Instance.IsHost()
            ? NetworkLifecycle.Instance.Server?.SelfId ?? 0
            : NetworkLifecycle.Instance.Client?.PlayerId ?? 0;
        DebugRuntime.Publish("item", "item.recall-requested",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: item.NetId.ToString(), data: new()
            {
                ["requestingPlayerId"] = localPlayerId,
                ["requestedSlot"] = requestedSlot,
                ["expectedRevision"] = item.AuthorityRevision,
                ["persistentOwnerPlayerId"] = item.PersistentOwnerPlayerId
            });

        if (!NetworkLifecycle.Instance.IsHost())
        {
            NetworkLifecycle.Instance.Client?.SendItemRecall(item.NetId, item.AuthorityRevision, requestedSlot);
            return true;
        }

        if (!NetworkLifecycle.Instance.Server.TryGetServerPlayer(localPlayerId, out ServerPlayer hostPlayer))
            return false;
        bool storedInLostAndFound = NetworkedLostAndFoundManager.Contains(item.NetId);
        if (!storedInLostAndFound &&
            !AuthoritativeItemRegistry.TryPrepareRecall(item, hostPlayer, requestedSlot,
                item.AuthorityRevision, out _, out string prepareRejection))
        {
            PublishHostRecallRejected(item, localPlayerId, prepareRejection);
            return false;
        }
        if (!storedInLostAndFound &&
            !item.TryPrepareLocalRecall(0, requestedSlot, out string localFailure))
        {
            if (AuthoritativeItemRegistry.TryCreateCorrectionSnapshot(item, out ItemUpdateData correction))
                item.ApplyServerCanonicalSnapshot(correction);
            item.CompleteLocalRecallPreparation(0, false);
            PublishHostRecallRejected(item, localPlayerId, localFailure);
            return false;
        }

        bool accepted = storedInLostAndFound
            ? NetworkedLostAndFoundManager.TryRetrieveForRecall(hostPlayer, item,
                item.AuthorityRevision, requestedSlot, out ItemUpdateData snapshot,
                out string rejectionReason)
            : AuthoritativeItemRegistry.TryCommitPreparedRecall(item, hostPlayer, requestedSlot,
                item.AuthorityRevision, out snapshot, out rejectionReason);
        if (!accepted)
        {
            if (!storedInLostAndFound)
            {
                if (AuthoritativeItemRegistry.TryCreateCorrectionSnapshot(item, out ItemUpdateData correction))
                    item.ApplyServerCanonicalSnapshot(correction);
                item.CompleteLocalRecallPreparation(0, false);
            }
            PublishHostRecallRejected(item, localPlayerId, rejectionReason);
            return false;
        }

        if (!storedInLostAndFound)
        {
            item.ApplyServerCanonicalSnapshot(snapshot);
            item.CompleteLocalRecallPreparation(0, true);
        }
        NetworkLifecycle.Instance.Server.SendItemUpdatePacket(snapshot);
        return true;
    }

    private static void PublishHostRecallRejected(NetworkedItem item, byte localPlayerId,
        string rejectionReason)
    {
        DebugRuntime.Publish("item", "item.recall-rejected", DebugRuntimeSide.Server,
            DebugSeverity.Warning, "Item", item.NetId.ToString(), new()
            {
                ["requestingPlayerId"] = localPlayerId,
                ["rejectionReason"] = rejectionReason ?? string.Empty
            });
    }

    public bool TryAdoptClientItem(ServerPlayer player, ItemAdoptionRequestData request,
        out ushort assignedNetId, out string rejectionReason)
    {
        assignedNetId = 0;
        rejectionReason = string.Empty;
        if (!NetworkLifecycle.Instance.IsHost() || player == null)
        {
            rejectionReason = "invalid-runtime-or-player";
            return false;
        }
        if (string.IsNullOrWhiteSpace(request.AdoptionToken) || request.AdoptionToken.Length > 64)
        {
            rejectionReason = "invalid-adoption-token";
            return false;
        }
        if (request.Snapshot == null || request.Snapshot.ItemNetId != 0)
        {
            rejectionReason = "invalid-adoption-snapshot";
            return false;
        }
        if (!TemporaryClientAdoptionCompatibility.AllowsHostRequest(request,
                IsJobDocumentPrefab, out rejectionReason))
            return false;
        ItemUpdateData.ItemUpdateType updateType = request.Snapshot.UpdateType;
        if ((updateType & ItemUpdateData.ItemUpdateType.FullSync) == 0 ||
            (updateType & (ItemUpdateData.ItemUpdateType.Create | ItemUpdateData.ItemUpdateType.Destroy)) != 0)
        {
            rejectionReason = "invalid-adoption-update-type";
            return false;
        }
        if (!IsFinite(request.Position) || !IsFinite(request.Rotation))
        {
            rejectionReason = "invalid-adoption-transform";
            return false;
        }
        if (request.Snapshot.ItemState is ItemState.Dropped or ItemState.Thrown &&
            (request.Position - player.AbsoluteWorldPosition).sqrMagnitude > 64f * 64f)
        {
            rejectionReason = "adoption-position-out-of-range";
            return false;
        }

        string token = request.AdoptionToken;
        if (TemporaryHostAdoptions.TryGetCompleted(player.PlayerId, token, out HostAdoptionOutcome previous))
        {
            assignedNetId = previous.AssignedNetId;
            rejectionReason = previous.RejectionReason;
            return previous.Accepted;
        }
        HostAdoptionBeginStatus begin = TemporaryHostAdoptions.TryBegin(player.PlayerId, token);
        if (begin != HostAdoptionBeginStatus.Started)
        {
            rejectionReason = begin == HostAdoptionBeginStatus.AlreadyPending
                ? "adoption-token-pending"
                : "invalid-adoption-token";
            return false;
        }
        if (!ItemPrefabs.TryGetValue(request.PrefabName ?? string.Empty, out InventoryItemSpec spec) || spec == null)
        {
            rejectionReason = "unknown-prefab";
            TemporaryHostAdoptions.Complete(player.PlayerId, token, false, 0, rejectionReason);
            return false;
        }
        GameObject gameObject = Instantiate(spec.gameObject, request.Position + WorldMover.currentMove, request.Rotation);
        NetworkedItem networkedItem = gameObject.GetOrAddComponent<NetworkedItem>();
        if (networkedItem == null || networkedItem.NetId == 0)
        {
            Destroy(gameObject);
            rejectionReason = "authoritative-id-allocation-failed";
            TemporaryHostAdoptions.Complete(player.PlayerId, token, false, 0, rejectionReason);
            return false;
        }

        assignedNetId = networkedItem.NetId;
        player.KnownItems[networkedItem] = NetworkLifecycle.Instance.Tick;
        player.AcknowledgedWorldItems.Add(networkedItem.NetId);
        ScheduleTrackedValueFinalization(networkedItem);
        request.Snapshot.ItemPosition = request.Position;
        request.Snapshot.ItemRotation = request.Rotation;
        networkedItem.ServerInitialiseAdoptedItem(player, request.Snapshot);
        TemporaryHostAdoptions.Complete(player.PlayerId, token, true, assignedNetId, string.Empty);
        return true;
    }

    private static bool IsFinite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);

    private static bool IsFinite(Quaternion value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z) &&
        !float.IsNaN(value.w) && !float.IsInfinity(value.w);

    internal void ResolveAuthoritativeBindingCollision(NetworkedItem incoming, ushort authoritativeId, Type trackedItemType)
    {
        if (incoming == null || authoritativeId == 0 || NetworkLifecycle.Instance.IsHost())
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
                DoNotCreateItem, out reason))
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
        TraceItem(authored ? "item.authored-projection-dormant" : "item.dynamic-projection-destroyed",
            netItem, new()
            {
                ["prefabName"] = netItem.Item?.InventorySpecs?.itemPrefabName ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["retiredNetId"] = retiredNetId
            });
        netItem.SetClientNetworkBinding(false);
        StorageIntegration.MoveTo(netItem.Item, StorageMembership.None);
        InventoryIntegration.RevokeMembership(netItem.gameObject, retiredNetId);
        InventoryIntegration.PurgeContainerMembership(netItem.gameObject);
        netItem.gameObject.SetActive(false);
        netItem.NetId = 0;
        if (authored)
        {
            netItem.ResetClientNetworkLifetime();
            DormantAuthoredClientProjections.Add(netItem);
            return;
        }

        DormantAuthoredClientProjections.Remove(netItem);
        Destroy(netItem.gameObject);
    }

}
