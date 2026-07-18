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
    // Client snapshot receipt, deferred projections, and special-item routing.

    //private void ProcessClientChanges(uint tick)
    //{
    //    List<ItemUpdateData> changedItems = new List<ItemUpdateData>();

    //    if(!ClientInitialised)
    //        return;

    //    foreach (var item in NetworkedItem.GetAll())
    //    {
    //        ItemUpdateData snapshot = item.GetSnapshot();
    //        if (snapshot != null)
    //        {
    //            changedItems.Add(snapshot);
    //        }
    //    }

    //    if (changedItems.Count > 0)
    //    {
    //        NetworkLifecycle.Instance.Client.SendItemsBulkUpdatePacket(changedItems);
    //    }
    //}

    private void ProcessReceivedAsClient(ItemUpdateData snapshot)
    {
        if (snapshot == null)
            return;
        if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Create &&
            RequiresLoadedWorldRegion(snapshot) && !IsWorldRegionReady(snapshot))
        {
            PendingWorldProjections[snapshot.ItemNetId] = new PendingWorldProjection { Create = snapshot };
            DebugRuntime.Publish("item-world", "world-item.projection-deferred", DebugRuntimeSide.Client,
                entityType: "Item", entityId: snapshot.ItemNetId.ToString(), data: new()
                {
                    ["prefabName"] = snapshot.PrefabName ?? string.Empty,
                    ["authoredItemKey"] = snapshot.AuthoredItemKey ?? string.Empty,
                    ["reason"] = "scene-and-terrain-region-not-loaded"
                });
            return;
        }
        if (snapshot.UpdateType != ItemUpdateData.ItemUpdateType.Create &&
            PendingWorldProjections.TryGetValue(snapshot.ItemNetId, out PendingWorldProjection pending))
        {
            if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Destroy)
            {
                PendingWorldProjections.Remove(snapshot.ItemNetId);
                NetworkLifecycle.Instance.Client.SendWorldItemProjectionAck(snapshot.ItemNetId,
                    snapshot.AuthorityRevision, false);
                DebugRuntime.Publish("item-world", "world-item.deferred-projection-retired",
                    DebugRuntimeSide.Client, entityType: "Item", entityId: snapshot.ItemNetId.ToString(),
                    data: DebugTrace.ItemSnapshotData(snapshot));
            }
            else
            {
                // The channel is reliable ordered. Retain the small ordered tail so a baseline
                // that becomes ready is immediately advanced to the latest canonical revision.
                if (pending.Updates.Count >= 64)
                    pending.Updates.RemoveAt(0);
                pending.Updates.Add(snapshot);
            }
            return;
        }

        NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem netItem);
        TraceSnapshot("item.snapshot-dispatch", snapshot);

        NetworkLifecycle.Instance.Client.LogDebug(() => $"NetworkedItemManager.ProcessReceivedAsClient() Update Type: {snapshot?.UpdateType}, ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
        if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Create)
        {
            if (WorldItemStableIdentity.IsValid(snapshot.AuthoredItemKey))
            {
                TryBindAuthoredClientProjection(snapshot);
                return;
            }
            if (snapshot.TransitionReason == ItemTransitionReason.LostAndFoundRetrieval)
                ClientLostAndFoundTombstones.Remove(snapshot.ItemNetId);
            // Lost and Found collection deliberately keeps the canonical client component bound
            // so a reserved inventory silhouette can still issue its normal Return request.
            // Restore that same representation in place; replacing it would clear its NetId and
            // BelongsToPlayer classification a second time.
            if (netItem != null && snapshot.TransitionReason == ItemTransitionReason.LostAndFoundRetrieval)
            {
                TraceItem("item.lost-and-found-client-projection-restored", netItem,
                    DebugTrace.ItemSnapshotData(snapshot));
                netItem.RestoreClientLostAndFoundProjection(snapshot);
                return;
            }

            // Job-created documents already own their authoritative ID. Applying the generic
            // snapshot to that representation is valid; replacing it would create a duplicate.
            if (netItem != null && DoNotCreateItem(netItem))
            {
                TraceItem("item.generic-create-bound-existing", netItem, new()
                {
                    ["trackedItemType"] = netItem.TrackedItemType?.FullName ?? string.Empty,
                    ["prefabName"] = snapshot.PrefabName ?? string.Empty
                });
                netItem.ReceiveSnapshot(snapshot);
                return;
            }

            // Reliable ordering normally creates the job document before its item metadata is
            // received. Keep the snapshot if scene/job construction is a frame late; generic item
            // creation here would duplicate the job system's canonical Unity object.
            if (netItem == null && IsJobDocumentPrefab(snapshot.PrefabName))
            {
                PendingSpecialItemSnapshots[snapshot.ItemNetId] = snapshot;
                Dictionary<string, object> deferredData = DebugTrace.ItemSnapshotData(snapshot);
                deferredData["reason"] = "waiting-for-job-lifecycle-object";
                DebugRuntime.Publish("item", "item.special-create-deferred", DebugRuntimeSide.Client,
                    entityType: "Item", entityId: snapshot.ItemNetId.ToString(), data: deferredData);
                return;
            }

            //if the item already exists we need to remove it
            if (netItem != null)
                RetireClientProjection(netItem, "create-replaced-existing-projection");

            CreateItem(snapshot);
        }
        else if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Destroy)
        {
            DebugDesyncDetector.Forget(snapshot.ItemNetId);
            Dictionary<string, object> destroyData = DebugTrace.ItemSnapshotData(snapshot);
            string entityId = snapshot.ItemNetId.ToString();
            destroyData["representationFound"] = netItem != null;
            DebugRuntime.Publish("item", "item.snapshot-received", DebugRuntimeSide.Client,
                entityType: "Item", entityId: entityId,
                data: new Dictionary<string, object>(destroyData, StringComparer.Ordinal));
            if (netItem != null)
                foreach (KeyValuePair<string, object> value in EntityDebugRegistry.ItemState(netItem))
                    destroyData[value.Key] = value.Value;
            DebugRuntime.Publish("item", "item.snapshot-apply.before", DebugRuntimeSide.Client,
                entityType: "Item", entityId: entityId,
                data: new Dictionary<string, object>(destroyData, StringComparer.Ordinal));
            bool lostAndFoundProjection =
                snapshot.TransitionReason == ItemTransitionReason.LostAndFoundCollection;
            bool coldContainerProjection =
                snapshot.TransitionReason == ItemTransitionReason.ContainerDeposit;
            if (lostAndFoundProjection)
            {
                ClientLostAndFoundTombstones.Add(snapshot.ItemNetId);
                foreach (NetworkedItem representation in NetworkedItem.GetAll()
                             .Where(value => value != null && value.NetId == snapshot.ItemNetId)
                             .Distinct().ToArray())
                    representation.ApplyClientLostAndFoundProjection(snapshot);
            }
            else
            {
                if (netItem != null)
                {
                    if (coldContainerProjection)
                        netItem.BeginColdStorageRetirement();
                    StorageIntegration.MoveTo(netItem.Item, StorageMembership.None);
                    InventoryIntegration.RevokeMembership(netItem.gameObject, netItem.NetId);
                    InventoryIntegration.PurgeContainerMembership(netItem.gameObject);
                }
                RetireClientProjection(netItem, snapshot.TransitionReason.ToString());
            }
            destroyData["destroyApplied"] = true;
            destroyData["applyResult"] = netItem == null ? "already-absent" :
                lostAndFoundProjection ? "lost-and-found-projection" :
                netItem.IsSceneAuthored ? "authored-projection-dormant" : "dynamic-projection-destroyed";
            destroyData["activeInHierarchy"] = netItem != null && netItem.gameObject.activeInHierarchy;
            destroyData["boundNetIdAfter"] = netItem?.NetId ?? 0;
            DebugRuntime.Publish("item", "item.snapshot-apply.after", DebugRuntimeSide.Client,
                entityType: "Item", entityId: entityId, data: destroyData);
            if (snapshot.TransitionReason == ItemTransitionReason.InterestRetirement)
                NetworkLifecycle.Instance.Client.SendWorldItemProjectionAck(snapshot.ItemNetId,
                    snapshot.AuthorityRevision, false);
        }
        else if (netItem != null)
        {
            netItem.ReceiveSnapshot(snapshot);
        }
        else
        {
            NetworkLifecycle.Instance.Client.LogError($"NetworkedItemManager.ProcessReceivedAsClient() NetworkedItem not found on client! Update Type: {snapshot.UpdateType}, ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
            DebugRuntime.Publish("item", "item.missing-local-representation", DebugRuntimeSide.Client, DebugSeverity.Error,
                "Item", snapshot.ItemNetId.ToString(), DebugValueSnapshotter.SnapshotObject(snapshot));
        }
    }

    private void ProcessPendingWorldProjections()
    {
        int budget = 8;
        foreach (KeyValuePair<ushort, PendingWorldProjection> pair in PendingWorldProjections.ToArray())
        {
            if (budget-- <= 0)
                break;
            PendingWorldProjection pending = pair.Value;
            if (pending?.Create == null || !IsWorldRegionReady(pending.Create))
                continue;

            PendingWorldProjections.Remove(pair.Key);
            bool projected = WorldItemStableIdentity.IsValid(pending.Create.AuthoredItemKey)
                ? TryBindAuthoredClientProjection(pending.Create, false)
                : CreateItem(pending.Create, false);
            if (!projected)
                continue;

            uint appliedRevision = pending.Create.AuthorityRevision;
            foreach (ItemUpdateData update in pending.Updates)
            {
                ProcessReceivedAsClient(update);
                appliedRevision = update.AuthorityRevision;
            }
            NetworkLifecycle.Instance.Client.SendWorldItemProjectionAck(pair.Key, appliedRevision, true);
            DebugRuntime.Publish("item-world", "world-item.deferred-projection-bound",
                DebugRuntimeSide.Client, entityType: "Item", entityId: pair.Key.ToString(), data: new()
                {
                    ["authorityRevision"] = appliedRevision,
                    ["queuedUpdateCount"] = pending.Updates.Count
                });
        }
    }

    private static bool RequiresLoadedWorldRegion(ItemUpdateData snapshot) =>
        snapshot != null && snapshot.ItemState is ItemState.Dropped or ItemState.Thrown or
            ItemState.Attached or ItemState.Removed;

    private static bool IsWorldRegionReady(ItemUpdateData snapshot)
    {
        try
        {
            return WorldStreamingInit.Instance != null &&
                   WorldStreamingInit.Instance.IsSceneAndTerrainRegionLoaded(
                       snapshot.ItemPosition + WorldMover.currentMove);
        }
        catch
        {
            return false;
        }
    }

    internal void ApplyPendingSpecialSnapshot(NetworkedItem item)
    {
        if (NetworkLifecycle.Instance.IsHost() || item == null || item.NetId == 0 ||
            !DoNotCreateItem(item) ||
            !PendingSpecialItemSnapshots.TryGetValue(item.NetId, out ItemUpdateData snapshot))
            return;

        PendingSpecialItemSnapshots.Remove(item.NetId);
        TraceItem("item.special-create-bound-existing", item, new()
        {
            ["prefabName"] = snapshot.PrefabName ?? string.Empty,
            ["authorityRevision"] = snapshot.AuthorityRevision
        });
        item.ReceiveSnapshot(snapshot);
    }

    private static bool IsJobDocumentPrefab(string prefabName) =>
        string.Equals(prefabName, nameof(JobOverview), StringComparison.Ordinal) ||
        string.Equals(prefabName, nameof(JobBooklet), StringComparison.Ordinal) ||
        string.Equals(prefabName, nameof(JobReport), StringComparison.Ordinal);

}
