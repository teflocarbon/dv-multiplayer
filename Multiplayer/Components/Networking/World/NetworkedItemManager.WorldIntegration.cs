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
using Multiplayer.Networking.Packets.Serverbound;

namespace Multiplayer.Components.Networking.World;

public partial class NetworkedItemManager
{
    // Authored catalogue binding, host cell indexes, spatial delegation, and replenishment hooks.
    private void EnsureAuthoredCatalogue()
    {
        if (authoredCatalogueBuilt)
            return;
        AuthoredCatalogue.Rebuild();
        authoredCatalogueBuilt = true;
        if (NetworkLifecycle.Instance.IsHost())
        {
            foreach (NetworkedItem item in NetworkedItem.GetAll().Where(value => value != null).Distinct().ToArray())
                RegisterHostWorldItem(item);
        }
    }

    internal (int Count, int Collisions, string Digest) GetAuthoredCatalogueSummary()
    {
        EnsureAuthoredCatalogue();
        return (AuthoredCatalogue.Count, AuthoredCatalogue.CollisionCount, AuthoredCatalogue.Digest());
    }

    internal bool TryGetAuthoredItem(string key, out NetworkedItem item)
    {
        EnsureAuthoredCatalogue();
        return AuthoredCatalogue.TryGet(key, out item);
    }

    internal void ReplaceAuthoredItemProjection(string key, NetworkedItem item)
    {
        EnsureAuthoredCatalogue();
        if (AuthoredCatalogue.TryGet(key, out NetworkedItem existing) && existing != null && existing != item)
        {
            UnregisterHostWorldItem(existing);
            existing.BeginColdStorageRetirement();
            AuthoritativeItemRegistry.Remove(existing.NetId);
            UnityEngine.Object.Destroy(existing.gameObject);
        }
        AuthoredCatalogue.Replace(key, item);
    }

    internal void RemoveAuthoredItemProjection(string key, NetworkedItem expected) =>
        AuthoredCatalogue.Remove(key, expected);

    internal void RegisterHostWorldItem(NetworkedItem item)
    {
        if (!NetworkLifecycle.Instance.IsHost() || item == null || item.NetId == 0)
            return;
        RefreshHostWorldItem(item);
        TrainItemWake?.ObserveCanonicalItem(item);
    }

    internal void UnregisterHostWorldItem(NetworkedItem item)
    {
        if (item == null)
            return;
        if (HostCellByItem.TryGetValue(item, out WorldItemCellCoord cell) &&
            HostItemsByCell.TryGetValue(cell, out HashSet<NetworkedItem> items))
        {
            items.Remove(item);
            if (items.Count == 0)
                HostItemsByCell.Remove(cell);
        }
        HostCellByItem.Remove(item);
        HostNonSpatialItems.Remove(item);
        HostPlayerCells.Clear();
    }

    internal void SetAuthoredCatalogueNegotiationResult(bool accepted)
    {
        authoredCatalogueAccepted = accepted;
    }

    private void RefreshHostWorldItem(NetworkedItem item)
    {
        if (item == null)
            return;
        if (HostCellByItem.TryGetValue(item, out WorldItemCellCoord oldCell) &&
            HostItemsByCell.TryGetValue(oldCell, out HashSet<NetworkedItem> oldItems))
        {
            oldItems.Remove(item);
            if (oldItems.Count == 0)
                HostItemsByCell.Remove(oldCell);
        }
        HostCellByItem.Remove(item);
        HostNonSpatialItems.Remove(item);

        if (WorldItemPersistenceManager.IsRestorePending(item))
            return;

        if (NetworkedLostAndFoundManager.Contains(item.NetId))
            return;
        if (AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record))
        {
            if (record.Placement == ItemPlacementKind.Destroyed)
                return;
            if (record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory or
                ItemPlacementKind.Container or ItemPlacementKind.Installed or
                ItemPlacementKind.TrainInterior or ItemPlacementKind.Attached or ItemPlacementKind.SnappedAttachment)
            {
                HostNonSpatialItems.Add(item);
                HostPlayerCells.Clear();
                return;
            }
            if (HasOwnerRecallClaim(record))
                HostNonSpatialItems.Add(item);
        }

        WorldItemCellCoord cell = WorldItemCellCoord.FromAbsolute(item.transform.position - WorldMover.currentMove);
        if (!HostItemsByCell.TryGetValue(cell, out HashSet<NetworkedItem> items))
            HostItemsByCell[cell] = items = new HashSet<NetworkedItem>();
        items.Add(item);
        HostCellByItem[item] = cell;
        // Re-evaluate the small per-player 3x3 union on the next tick. This is O(players), not a
        // rescan of the world catalogue, and handles a thrown item crossing a cell boundary.
        HostPlayerCells.Clear();
    }

    internal void RefreshHostSpatialPosition(NetworkedItem item, Vector3 absolutePosition,
        ItemWorldParentKind parentKind)
    {
        if (!NetworkLifecycle.Instance.IsHost() || item == null)
            return;
        bool hadPreviousCell = HostCellByItem.TryGetValue(item, out WorldItemCellCoord previous);
        if (parentKind == ItemWorldParentKind.World)
        {
            WorldItemCellCoord unchanged = WorldItemCellCoord.FromAbsolute(absolutePosition);
            if (hadPreviousCell && previous.Equals(unchanged))
                return;
        }
        else if (!hadPreviousCell && HostNonSpatialItems.Contains(item))
            return;

        if (hadPreviousCell &&
            HostItemsByCell.TryGetValue(previous, out HashSet<NetworkedItem> previousItems))
        {
            previousItems.Remove(item);
            if (previousItems.Count == 0) HostItemsByCell.Remove(previous);
        }
        HostCellByItem.Remove(item);
        HostNonSpatialItems.Remove(item);
        if (parentKind != ItemWorldParentKind.World)
        {
            HostNonSpatialItems.Add(item);
            HostPlayerCells.Clear();
            DebugRuntime.Publish("item-spatial", "item.spatial-cell-changed", DebugRuntimeSide.Server,
                entityType: "Item", entityId: item.NetId.ToString(), data: new()
                {
                    ["previousCell"] = hadPreviousCell ? previous.ToString() : string.Empty,
                    ["currentCell"] = string.Empty,
                    ["worldParentKind"] = parentKind.ToString()
                });
            return;
        }
        WorldItemCellCoord cell = WorldItemCellCoord.FromAbsolute(absolutePosition);
        HostCellByItem[item] = cell;
        if (!HostItemsByCell.TryGetValue(cell, out HashSet<NetworkedItem> items))
            HostItemsByCell[cell] = items = new HashSet<NetworkedItem>();
        items.Add(item);
        HostPlayerCells.Clear();
        DebugRuntime.Publish("item-spatial", "item.spatial-cell-changed", DebugRuntimeSide.Server,
            entityType: "Item", entityId: item.NetId.ToString(), data: new()
            {
                ["previousCell"] = hadPreviousCell ? previous.ToString() : string.Empty,
                ["currentCell"] = cell.ToString(),
                ["worldParentKind"] = parentKind.ToString()
            });
    }

    internal void OnAuthoritativeItemTransition(NetworkedItem item, ItemUpdateData snapshot,
        AuthoritativeItemRegistry.Record record, ServerPlayer actor, ItemPlacementKind previousPlacement,
        bool appliesPlacement)
    {
        TrainItemWake?.OnAuthoritativeTransition(item, record, previousPlacement, appliesPlacement);
        Spatial?.OnAuthoritativeTransitionCommitted(item, snapshot, record, actor, previousPlacement,
            appliesPlacement);
        Replenishment?.OnAuthoritativeTransition(item, record, previousPlacement, appliesPlacement);
    }

    internal void DetachClientAuthoredProjectionForPossession(NetworkedItem item, ItemUpdateData snapshot)
    {
        if (item == null || snapshot == null ||
            snapshot.ItemState is not (ItemState.InHand or ItemState.InInventory) ||
            !AuthoredWorldItemReplenishmentManager.IsEligibleAuthoredOfficeProp(item))
            return;
        string key = item.DetachAuthoredItemKey();
        if (WorldItemStableIdentity.IsValid(key))
        {
            AuthoredCatalogue.Remove(key, item);
            ClientDetachedAuthoredSlots.Add(key);
        }
    }

    internal void ReceiveItemSpatialSample(ItemSpatialStateData state, ServerPlayer sender) =>
        Spatial?.ReceiveClientSample(state, sender);

    internal void ReceiveItemSpatialSettlement(ItemSpatialStateData state, ServerPlayer sender) =>
        Spatial?.ReceiveClientSettlement(state, sender);

    internal void ReceiveItemSpatialLease(ClientboundItemSpatialLeasePacket packet) =>
        Spatial?.ReceiveClientLease(packet);

    internal void ReceiveItemSpatialSample(ItemSpatialStateData state) =>
        Spatial?.ReceiveRelayedSample(state);

    internal void ReceiveItemSpatialCommit(ClientboundItemSpatialCommitPacket packet) =>
        Spatial?.ReceiveSpatialCommit(packet);

    internal void AppendSpatialDebugState(ushort itemNetId, Dictionary<string, object> state) =>
        AppendItemPhysicsDebugState(itemNetId, state);

    private void AppendItemPhysicsDebugState(ushort itemNetId, Dictionary<string, object> state)
    {
        Spatial?.AppendDebugState(itemNetId, state);
        TrainItemWake?.AppendDebugState(itemNetId, state);
    }

    internal void OnTrainItemSpatialSettled(NetworkedItem item, ItemSpatialStateData state,
        AuthoritativeItemRegistry.Record record) =>
        TrainItemWake?.OnSpatialSettled(item, state, record);

    internal void ObserveTrainItemCollision(NetworkedItem source, NetworkedItem target,
        Collision collision) => TrainItemWake?.ObserveCollision(source, target, collision);

    internal void ReceiveItemTrainWakeWitness(ServerboundItemTrainWakeWitnessPacket packet,
        ServerPlayer sender) => TrainItemWake?.ReceiveImpactWitness(packet, sender);

    internal void OnTrainItemRemoved(ushort itemNetId) => TrainItemWake?.OnItemRemoved(itemNetId);

    internal bool ForceTrainItemWake(ushort itemNetId, TrainItemWakeReason reason,
        Vector3 initialLocalVelocity, out string rejection)
    {
        if (TrainItemWake != null)
            return TrainItemWake.ForceWake(itemNetId, reason, initialLocalVelocity, out rejection);
        rejection = "train-item-wake-manager-unavailable";
        return false;
    }

    internal Dictionary<string, object> TrainItemWakeSnapshot(ushort itemNetId = 0,
        ushort carNetId = 0) => TrainItemWake?.Snapshot(itemNetId, carNetId) ?? new();

#if DEBUG
    internal bool DebugArrangeSettledTrainItem(ushort itemNetId, ushort carNetId,
        Vector3 parentLocalPosition, Quaternion parentLocalRotation, out string rejection)
    {
        if (Spatial != null)
            return Spatial.DebugArrangeSettledTrainItem(itemNetId, carNetId,
                parentLocalPosition, parentLocalRotation, out rejection);
        rejection = "spatial-manager-unavailable";
        return false;
    }
#endif

    internal bool TryGetLatestSpatialState(ushort itemNetId, out ItemSpatialStateData state)
    {
        state = null;
        return Spatial?.TryGetLatestAccepted(itemNetId, out state) == true;
    }

    internal bool HasActiveSpatialState(ushort itemNetId) =>
        Spatial?.TryGetLatestAccepted(itemNetId, out _) == true;

    internal bool TryGetCommittedSpatialState(ushort itemNetId, out ItemSpatialStateData state)
    {
        state = null;
        return Spatial?.TryGetCommitted(itemNetId, out state) == true;
    }

    internal bool ResetAuthoredItemToBaseline(NetworkedItem item,
        AuthoritativeItemRegistry.Record record) =>
        Spatial?.ResetAuthoredItemToBaseline(item, record) == true;

    internal void OnSpatialPlayerDisconnected(byte playerId) => Spatial?.OnPlayerDisconnected(playerId);

    private bool TryBindAuthoredClientProjection(ItemUpdateData snapshot, bool acknowledge = true)
    {
        EnsureAuthoredCatalogue();
        if (!authoredCatalogueAccepted)
        {
            DebugRuntime.Publish("item-world", "world-item.authored-binding-quarantined", DebugRuntimeSide.Client,
                DebugSeverity.Error, "Item", snapshot.ItemNetId.ToString(), new()
                {
                    ["authoredItemKey"] = snapshot.AuthoredItemKey,
                    ["prefabName"] = snapshot.PrefabName ?? string.Empty,
                    ["reason"] = "catalogue-not-accepted"
                });
            return false;
        }
        if (!AuthoredCatalogue.TryGet(snapshot.AuthoredItemKey, out NetworkedItem authored) || authored == null)
        {
            // A replenishable authored slot deliberately loses its original local scene
            // projection when that object becomes a persistent carried item. Only a key
            // recorded by that local transition may create a fresh projection; every other
            // missing authored key remains an integrity failure.
            if (ClientDetachedAuthoredSlots.Contains(snapshot.AuthoredItemKey) &&
                CreateItem(snapshot, false) &&
                NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem replacement) &&
                replacement != null)
            {
                ClientDetachedAuthoredSlots.Remove(snapshot.AuthoredItemKey);
                replacement.AdoptAuthoredItemKey(snapshot.AuthoredItemKey,
                    replenishableOfficeSlot: true);
                if (acknowledge)
                    NetworkLifecycle.Instance.Client.SendWorldItemProjectionAck(snapshot.ItemNetId,
                        snapshot.AuthorityRevision, true);
                DebugRuntime.Publish("item-world", "world-item.authored-slot-replacement-projected",
                    DebugRuntimeSide.Client, entityType: "Item",
                    entityId: snapshot.ItemNetId.ToString(), data: new()
                    {
                        ["authoredItemKey"] = snapshot.AuthoredItemKey,
                        ["prefabName"] = snapshot.PrefabName ?? string.Empty
                    });
                return true;
            }
            DebugRuntime.Publish("item-world", "world-item.authored-binding-missing", DebugRuntimeSide.Client,
                DebugSeverity.Error, "Item", snapshot.ItemNetId.ToString(), new()
                {
                    ["authoredItemKey"] = snapshot.AuthoredItemKey,
                    ["prefabName"] = snapshot.PrefabName ?? string.Empty,
                    ["catalogueDigest"] = AuthoredCatalogue.Digest()
                });
            return false;
        }

        if (NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem collision) && collision != null && collision != authored)
            RetireClientProjection(collision, "authored-binding-collision");
        DormantAuthoredClientProjections.Remove(authored);
        if (authored.NetId != 0 && authored.NetId != snapshot.ItemNetId)
            authored.ResetClientNetworkLifetime();
        authored.NetId = snapshot.ItemNetId;
        authored.SetClientNetworkBinding(true);
        authored.gameObject.SetActive(true);
        authored.ReceiveSnapshot(snapshot);
        if (acknowledge)
            NetworkLifecycle.Instance.Client.SendWorldItemProjectionAck(snapshot.ItemNetId,
                snapshot.AuthorityRevision, true);
        DebugRuntime.Publish("item-world", "world-item.authored-projection-bound", DebugRuntimeSide.Client,
            entityType: "Item", entityId: snapshot.ItemNetId.ToString(), data: new()
            {
                ["authoredItemKey"] = snapshot.AuthoredItemKey,
                ["prefabName"] = snapshot.PrefabName ?? string.Empty
            });
        return true;
    }

#if DEBUG
    internal void PurgeClientFixtureRepresentation(NetworkedItem netItem)
    {
        if (netItem == null || NetworkLifecycle.Instance.IsHost())
            return;
        ushort netId = netItem.NetId;
        bool isCanonical = netId != 0 && NetworkedItem.TryGet(netId,
            out NetworkedItem canonical) && canonical == netItem;
        DormantAuthoredClientProjections.Remove(netItem);
        netItem.BeginColdStorageRetirement();
        StorageIntegration.MoveTo(netItem.Item, StorageMembership.None);
        InventoryIntegration.RevokeMembership(netItem.gameObject,
            isCanonical ? netId : (ushort)0);
        InventoryIntegration.PurgeContainerMembership(netItem.gameObject);
        netItem.SetClientNetworkBinding(false);
        netItem.gameObject.SetActive(false);
        netItem.NetId = 0;
        Destroy(netItem.gameObject);
    }
#endif

    private static void TraceSnapshot(string eventName, ItemUpdateData snapshot)
    {
        if (!DebugRuntime.EnabledFor("item") || snapshot == null) return;
        DebugRuntime.Publish("item", eventName, NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: snapshot.ItemNetId.ToString(), data: DebugTrace.ItemSnapshotData(snapshot));
    }

    private static void TraceItem(string eventName, NetworkedItem item, Dictionary<string, object> data)
    {
        if (!DebugRuntime.EnabledFor("item") || item == null) return;
        DebugRuntime.Publish("item", eventName, NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: item.NetId.ToString(), data: data);
    }

}
