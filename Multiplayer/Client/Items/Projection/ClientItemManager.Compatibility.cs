using Multiplayer.Core.Items.Projection;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV.InventorySystem;
using Multiplayer.Client.Items.LiveState;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Core.Replication;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using UnityEngine;

namespace Multiplayer.Client.Items.Projection;

internal sealed partial class ClientItemManager
{
    // Dynamic projection creation and unbound local items. A client never makes an item of its own:
    // the server makes every one (owned-items.md, PR 5), so an unbound item is an authored scene
    // projection, kept dormant, or destroyed.
    // Dynamic client projections are fresh instances owned by one host-authoritative logical
    // lifetime.
    private bool CreateItem(ItemUpdate snapshot, bool acknowledge = true)
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
                snapshot.AuthorityRevision, ProjectionAck.Projected);
        return true;
    }

    /// <summary>The world has loaded: classify every item this game made before the host's arrive.</summary>
    internal void QuarantineUnboundClientItems()
    {
        shell.EnsureAuthoredCatalogue();

        // The host owns canonical existence. Preserve exact authored scene projections in a
        // dormant state and destroy every other unbound client object.
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
        item.Client()?.GateAsSceneObjectAwaitingHostCreate();
        NetworkedItemManager.TraceItem("item.unbound-classified", item, new()
        {
            ["classification"] = item.IsSceneAuthored ? "host-authored-projection" : "scene-authored-clutter"
        });
        if (PendingUnboundClientItemSet.Add(item))
            PendingUnboundClientItems.Enqueue(item);
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
