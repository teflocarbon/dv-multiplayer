using System.Collections.Generic;
using System.Linq;
using DV.CabControls;
using DV.Customization.Gadgets;
using DV.Items;
using DV.Utils;
using UnityEngine;
using JetBrains.Annotations;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;
using System;
using DV;
using DV.InventorySystem;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items;
using Multiplayer.Core.Replication;
using DV.Booklets;

namespace Multiplayer.Shared.Items;

public partial class NetworkedItemManager
{
    // Authored catalogue binding and spatial delegation. The host's cell index and the
    // replenishment hooks are the server's (ServerItemManager.WorldIndex.cs).
    private void EnsureAuthoredCatalogue()
    {
        if (authoredCatalogueBuilt)
            return;
        AuthoredCatalogue.Rebuild();
        authoredCatalogueBuilt = true;
        if (NetworkLifecycle.Instance.IsHost() && ServerPart is { } server)
        {
            foreach (NetworkedItem item in NetworkedItem.GetAll().Where(value => value != null).Distinct().ToArray())
                server.RegisterHostWorldItem(item);
        }
    }

    internal (int Count, int Collisions, string Digest) GetAuthoredCatalogueSummary()
    {
        EnsureAuthoredCatalogue();
        return (AuthoredCatalogue.BaselineCount, AuthoredCatalogue.BaselineCollisionCount,
            AuthoredCatalogue.BaselineDigest);
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
            ServerPart?.UnregisterHostWorldItem(existing);
            existing.NoteRemovalCause(new ChangeCause<ItemRemovalReason>(ItemRemovalReason.AuthoredSlotReplaced,
                tick: NetworkLifecycle.Instance?.Tick ?? 0, detail: $"slot {key} now netId {item?.NetId ?? 0}"));
            existing.BeginColdStorageRetirement();
            ServerPart?.ForgetRecord(existing.NetId);
            UnityEngine.Object.Destroy(existing.gameObject);
        }
        AuthoredCatalogue.Replace(key, item);
    }

    internal void RemoveAuthoredItemProjection(string key, NetworkedItem expected) =>
        AuthoredCatalogue.Remove(key, expected);

    // The host's cell index is the server's (ServerItemManager.WorldIndex.cs); these are for the
    // Shared code that changes where an item is.
    internal void RegisterHostWorldItem(NetworkedItem item) => ServerPart?.RegisterHostWorldItem(item);

    internal void RefreshHostSpatialPosition(NetworkedItem item, Vector3 absolutePosition,
        ItemWorldParentKind parentKind) => ServerPart?.RefreshHostSpatialPosition(item, absolutePosition, parentKind);

    internal void SetAuthoredCatalogueNegotiationResult(bool accepted)
    {
        authoredCatalogueAccepted = accepted;
    }

    internal void DetachClientAuthoredProjectionForPossession(NetworkedItem item, ItemUpdateData snapshot)
    {
        if (item == null || snapshot == null ||
            snapshot.ItemState is not (ItemState.InHand or ItemState.InInventory) ||
            !IsEligibleAuthoredOfficeProp(item))
            return;
        string key = item.DetachAuthoredItemKey();
        if (WorldItemStableIdentity.IsValid(key))
        {
            AuthoredCatalogue.Remove(key, item);
            ClientDetachedAuthoredSlots.Add(key);
        }
    }

    /// <summary>
    /// A scene-authored office prop whose slot the host refills once it's taken away
    /// (AuthoredWorldItemReplenishmentManager); a client lets go of the slot when it takes one.
    /// </summary>
    internal static bool IsEligibleAuthoredOfficeProp(NetworkedItem item)
    {
        if (item?.Item?.InventorySpecs == null || !item.IsSceneAuthored ||
            item.Item.InventorySpecs.BelongsToPlayer || item.Item.InventorySpecs.IsEssential || item.Item.IsSnapped ||
            item.GetComponent<ItemContainer>() != null || item.GetComponent<GadgetItem>() != null ||
            item.GetComponent<JobBooklet>() != null || item.GetComponent<JobOverview>() != null ||
            item.GetComponent<JobReport>() != null)
            return false;
        return item.IsReplenishableAuthoredOfficeSlot && !item.IsAuthoredShopSlot;
    }

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
        if (ServerPart != null)
            ServerPart.AppendTrainWakeDebugState(itemNetId, state);
        else if (state != null)
        {
            // Train items are woken by the host only: a client's copy is never settled or pending.
            state["trainWakeSettled"] = false;
            state["trainWakePending"] = false;
        }
    }

    // The train-wake manager's thresholds for an impact worth waking items for; the simulator
    // checks them before it reports one, and the host again when it takes the report.
    internal const float TrainItemImpactRelativeSpeedThreshold = 1.25f;
    internal const float TrainItemImpactImpulseThreshold = 0.5f;
    private static uint nextTrainWakeWitnessId;

    /// <summary>
    /// The game simulating a train item saw it hit another item settled in the same car: it
    /// reports the impact to the host, whose train-wake manager decides whether the target wakes.
    /// </summary>
    internal void ObserveTrainItemCollision(NetworkedItem source, NetworkedItem target, Collision collision)
    {
        if (source == null || target == null || source == target || collision == null ||
            NetworkLifecycle.Instance == null || Spatial == null ||
            !Spatial.TryGetActiveLeaseIdentity(source.NetId, out uint sourceRevision,
                out uint sourceEpoch, out byte simulator, out ItemSpatialStateData sourceState) ||
            sourceState.WorldParentKind != ItemWorldParentKind.TrainInterior ||
            sourceState.WorldParentNetId == 0 ||
            !Spatial.TryGetCommitted(target.NetId, out ItemSpatialStateData targetState) ||
            targetState.WorldParentKind != ItemWorldParentKind.TrainInterior ||
            targetState.WorldParentNetId != sourceState.WorldParentNetId)
            return;

        byte localPlayerId = NetworkRole.LocalPlayerId;
        if (simulator != localPlayerId)
            return;
        Vector3 relativeVelocity = collision.relativeVelocity;
        Vector3 impulse = collision.impulse;
        if (relativeVelocity.magnitude < TrainItemImpactRelativeSpeedThreshold &&
            impulse.magnitude < TrainItemImpactImpulseThreshold)
            return;
        Vector3 contact = collision.contactCount > 0 ? collision.GetContact(0).point :
            target.transform.position;
        ServerboundItemTrainWakeWitnessPacket packet = new()
        {
            WitnessId = unchecked(++nextTrainWakeWitnessId),
            SourceItemNetId = source.NetId,
            SourceAuthorityRevision = sourceRevision,
            SourceSimulationEpoch = sourceEpoch,
            TargetItemNetId = target.NetId,
            TargetAuthorityRevision = targetState.AuthorityRevision,
            TrainCarNetId = sourceState.WorldParentNetId,
            SourceTick = NetworkLifecycle.Instance.Tick,
            RelativeVelocity = relativeVelocity,
            Impulse = impulse,
            AbsoluteContactPoint = contact - WorldMover.currentMove
        };
        if (NetworkLifecycle.Instance.IsHost())
            ServerPart?.ReceiveLocalTrainWakeWitness(packet);
        else
            ItemClient.Current.SendItemTrainWakeWitness(packet);
    }

    internal void OnTrainItemRemoved(ushort itemNetId) => ServerPart?.OnTrainItemRemoved(itemNetId);

    internal bool TryGetLatestSpatialState(ushort itemNetId, out ItemSpatialStateData state)
    {
        state = null;
        return Spatial?.TryGetLatestAccepted(itemNetId, out state) == true;
    }

    internal bool HasActiveSpatialState(ushort itemNetId) =>
        Spatial?.TryGetLatestAccepted(itemNetId, out _) == true;

    internal bool IsSpatiallySimulatedRemotely(ushort itemNetId) =>
        Spatial?.IsSimulatedRemotely(itemNetId) == true;

    internal bool TryGetCommittedSpatialState(ushort itemNetId, out ItemSpatialStateData state)
    {
        state = null;
        return Spatial?.TryGetCommitted(itemNetId, out state) == true;
    }

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
                    ItemClient.Current.SendWorldItemProjectionAck(snapshot.ItemNetId,
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
            ItemClient.Current.SendWorldItemProjectionAck(snapshot.ItemNetId,
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

    internal static void TraceSnapshot(string eventName, ItemUpdateData snapshot)
    {
        if (!DebugRuntime.EnabledFor("item") || snapshot == null) return;
        DebugRuntime.Publish("item", eventName, NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: snapshot.ItemNetId.ToString(), data: DebugTrace.ItemSnapshotData(snapshot));
    }

    internal static void TraceItem(string eventName, NetworkedItem item, Dictionary<string, object> data)
    {
        if (!DebugRuntime.EnabledFor("item") || item == null) return;
        DebugRuntime.Publish("item", eventName, NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: item.NetId.ToString(), data: data);
    }

}
