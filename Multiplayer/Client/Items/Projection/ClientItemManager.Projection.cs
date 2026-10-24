using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Client.Items.ChangeCauses;
using Multiplayer.Client.Items.Inventory;
using Multiplayer.Client.Items.LiveState;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;

namespace Multiplayer.Client.Items.Projection;

internal sealed partial class ClientItemManager
{
    // Client snapshot receipt, deferred projections, and special-item routing.

    private void ProcessReceivedAsClient(ItemUpdate snapshot)
    {
        if (snapshot == null)
            return;
        InventoryRows.Observe(snapshot);
        // The local inventory is stand-ins (D14): one going in is placed, one leaving is made.
        if (ReceiveAsStandIn(snapshot, null))
            return;
        NetworkedItem leftStandIn = LeaveStandIn(snapshot);
        // Before the item applies it, while the item still says whose it was.
        if (snapshot.UpdateType == ItemUpdate.ItemUpdateType.Destroy || snapshot.MoveCause.HasValue)
        {
            NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem described);
            ClientItemChangeLog.OnSnapshot(described, snapshot, NetworkLifecycle.Instance?.LocalSession?.PlayerId ?? 0);
        }
        if (snapshot.UpdateType == ItemUpdate.ItemUpdateType.Create &&
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
        if (snapshot.UpdateType != ItemUpdate.ItemUpdateType.Create &&
            PendingWorldProjections.TryGetValue(snapshot.ItemNetId, out PendingWorldProjection pending))
        {
            if (snapshot.UpdateType == ItemUpdate.ItemUpdateType.Destroy)
            {
                PendingWorldProjections.Remove(snapshot.ItemNetId);
                ItemClient.Current.SendWorldItemProjectionAck(snapshot.ItemNetId,
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
        NetworkedItemManager.TraceSnapshot("item.snapshot-dispatch", snapshot);

        (NetworkLifecycle.Instance.LocalSession as NetworkManager)?.LogDebug(() => $"NetworkedItemManager.ProcessReceivedAsClient() Update Type: {snapshot?.UpdateType}, ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
        if (snapshot.UpdateType == ItemUpdate.ItemUpdateType.Create)
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
                NetworkedItemManager.TraceItem("item.lost-and-found-client-projection-restored", netItem,
                    DebugTrace.ItemSnapshotData(snapshot));
                netItem.Client()?.RestoreLostAndFoundProjection(snapshot);
                return;
            }

            // Job-created documents already own their authoritative ID. Applying the generic
            // snapshot to that representation is valid; replacing it would create a duplicate.
            if (netItem != null && shell.DoNotCreateItem(netItem))
            {
                NetworkedItemManager.TraceItem("item.generic-create-bound-existing", netItem, new()
                {
                    ["trackedItemType"] = netItem.TrackedItemType?.FullName ?? string.Empty,
                    ["prefabName"] = snapshot.PrefabName ?? string.Empty
                });
                netItem.Client()?.SetNetworkBinding(true);
                netItem.gameObject.SetActive(true);
                netItem.ReceiveSnapshot(snapshot);
                ItemClient.Current.SendWorldItemProjectionAck(snapshot.ItemNetId,
                    snapshot.AuthorityRevision, true);
                return;
            }

            // Reliable ordering normally creates the job document before its item metadata is
            // received. Keep the snapshot if scene/job construction is a frame late; generic item
            // creation here would duplicate the job system's canonical Unity object.
            if (netItem == null && NetworkedItemManager.IsJobDocumentPrefab(snapshot.PrefabName))
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
        else if (snapshot.UpdateType == ItemUpdate.ItemUpdateType.Destroy)
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
                snapshot.TransitionReason is ItemTransitionReason.ContainerDeposit or ItemTransitionReason.WentCold;
            bool dormantJobDocument =
                snapshot.TransitionReason == ItemTransitionReason.InterestRetirement &&
                netItem != null && shell.DoNotCreateItem(netItem);
            if (lostAndFoundProjection)
            {
                ClientLostAndFoundTombstones.Add(snapshot.ItemNetId);
                foreach (NetworkedItem representation in NetworkedItem.GetAll()
                             .Where(value => value != null && value.NetId == snapshot.ItemNetId)
                             .Distinct().ToArray())
                    representation.Client()?.ApplyLostAndFoundProjection(snapshot);
            }
            else
            {
                ClientLostAndFoundTombstones.Remove(snapshot.ItemNetId);
                if (netItem != null)
                {
                    if (coldContainerProjection)
                        netItem.BeginColdStorageRetirement();
                    StorageIntegration.MoveTo(netItem.Item, StorageMembership.None);
                    InventoryIntegration.RevokeMembership(netItem.gameObject, netItem.NetId);
                    InventoryIntegration.PurgeContainerMembership(netItem.gameObject);
                }
                if (dormantJobDocument)
                    SuspendJobDocumentProjection(netItem, snapshot);
                else
                    RetireClientProjection(netItem, snapshot.TransitionReason.ToString());
            }
            destroyData["destroyApplied"] = true;
            destroyData["applyResult"] = netItem == null ? "already-absent" :
                lostAndFoundProjection ? "lost-and-found-projection" :
                dormantJobDocument ? "job-document-projection-dormant" :
                netItem.IsSceneAuthored ? "authored-projection-dormant" : "dynamic-projection-destroyed";
            destroyData["activeInHierarchy"] = netItem != null && netItem.gameObject.activeInHierarchy;
            destroyData["boundNetIdAfter"] = netItem?.NetId ?? 0;
            DebugRuntime.Publish("item", "item.snapshot-apply.after", DebugRuntimeSide.Client,
                entityType: "Item", entityId: entityId, data: destroyData);
            if (snapshot.TransitionReason == ItemTransitionReason.InterestRetirement)
                ItemClient.Current.SendWorldItemProjectionAck(snapshot.ItemNetId,
                    snapshot.AuthorityRevision, false);
        }
        else if (netItem != null)
        {
            netItem.ReceiveSnapshot(snapshot);
            if (leftStandIn != null && leftStandIn == netItem)
                DropRefusedPutAway(netItem, snapshot);
        }
        else
        {
            (NetworkLifecycle.Instance.LocalSession as NetworkManager)?.LogError($"NetworkedItemManager.ProcessReceivedAsClient() NetworkedItem not found on client! Update Type: {snapshot.UpdateType}, ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
            DebugRuntime.Publish("item", "item.missing-local-representation", DebugRuntimeSide.Client, DebugSeverity.Error,
                "Item", snapshot.ItemNetId.ToString(), DebugValueSnapshotter.SnapshotObject(snapshot));
        }
    }

    private void ProcessPendingWorldProjections()
    {
        // The budget limits projections per tick, not readiness checks: counting checks let a
        // handful of never-ready entries at the front of the table block every later one forever.
        int budget = 8;
        foreach (KeyValuePair<ushort, PendingWorldProjection> pair in PendingWorldProjections.ToArray())
        {
            if (budget <= 0)
                break;
            PendingWorldProjection pending = pair.Value;
            if (pending?.Create == null || !IsWorldRegionReady(pending.Create))
                continue;

            budget--;
            PendingWorldProjections.Remove(pair.Key);
            bool projected = WorldItemStableIdentity.IsValid(pending.Create.AuthoredItemKey)
                ? TryBindAuthoredClientProjection(pending.Create, false)
                : CreateItem(pending.Create, false);
            if (!projected)
            {
                // Tell the host, or it believes we have the item and only ever sends updates.
                ItemClient.Current.SendWorldItemProjectionAck(pair.Key,
                    pending.Create.AuthorityRevision, false);
                continue;
            }

            uint appliedRevision = pending.Create.AuthorityRevision;
            foreach (ItemUpdate update in pending.Updates)
            {
                ProcessReceivedAsClient(update);
                appliedRevision = update.AuthorityRevision;
            }
            ItemClient.Current.SendWorldItemProjectionAck(pair.Key, appliedRevision, true);
            DebugRuntime.Publish("item-world", "world-item.deferred-projection-bound",
                DebugRuntimeSide.Client, entityType: "Item", entityId: pair.Key.ToString(), data: new()
                {
                    ["authorityRevision"] = appliedRevision,
                    ["queuedUpdateCount"] = pending.Updates.Count
                });
        }
    }

    private static bool RequiresLoadedWorldRegion(ItemUpdate snapshot) =>
        snapshot != null && snapshot.ItemState is ItemState.Dropped or ItemState.Thrown or
            ItemState.Attached;

    private static bool IsWorldRegionReady(ItemUpdate snapshot)
    {
        // Items riding a car are placed relative to it; their world position in the Create is
        // where the car was when it was sent and may be long gone. What they need is the car.
        if (snapshot.ItemState == ItemState.Attached)
            return NetworkedTrainCar.TryGet(snapshot.CarNetId, out TrainCar attachedCar) && attachedCar != null;
        if (snapshot.WorldParentKind == ItemWorldParentKind.TrainInterior)
            return NetworkedTrainCar.TryGet(snapshot.WorldParentNetId, out TrainCar parentCar) && parentCar != null;

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

    public void ApplyPendingSpecialSnapshot(NetworkedItem item)
    {
        if (item == null || item.NetId == 0 ||
            !shell.DoNotCreateItem(item) ||
            !PendingSpecialItemSnapshots.TryGetValue(item.NetId, out ItemUpdate snapshot))
            return;

        PendingSpecialItemSnapshots.Remove(item.NetId);
        NetworkedItemManager.TraceItem("item.special-create-bound-existing", item, new()
        {
            ["prefabName"] = snapshot.PrefabName ?? string.Empty,
            ["authorityRevision"] = snapshot.AuthorityRevision
        });
        item.Client()?.SetNetworkBinding(true);
        item.gameObject.SetActive(true);
        item.ReceiveSnapshot(snapshot);
        ItemClient.Current.SendWorldItemProjectionAck(snapshot.ItemNetId,
            snapshot.AuthorityRevision, true);
    }

    private static void SuspendJobDocumentProjection(NetworkedItem item, ItemUpdate snapshot)
    {
        item.Client()?.SetNetworkBinding(false);
        item.gameObject.SetActive(false);
        DebugRuntime.Publish("item", "item.job-document-projection-dormant",
            DebugRuntimeSide.Client, entityType: "Item", entityId: item.NetId.ToString(),
            data: new()
            {
                ["trackedItemType"] = item.TrackedItemType?.FullName ?? string.Empty,
                ["authorityRevision"] = snapshot.AuthorityRevision,
                ["reason"] = snapshot.TransitionReason.ToString()
            });
    }
}
