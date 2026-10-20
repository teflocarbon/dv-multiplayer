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
    // The authored catalogue and spatial delegation. Binding the host's authored items to this
    // game's copies is the remote client's part's (ClientItemManager.WorldIntegration.cs). The
    // host's cell index and the replenishment hooks are the server's (ServerItemManager.WorldIndex.cs).
    internal void EnsureAuthoredCatalogue()
    {
        if (authoredCatalogueBuilt)
            return;
        AuthoredCatalogue.Rebuild();
        authoredCatalogueBuilt = true;
        if (ServerPart is { } server)
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

    internal string AuthoredCatalogueDigest() => AuthoredCatalogue.Digest();

    internal void RemoveAuthoredItemProjection(string key, NetworkedItem expected) =>
        AuthoredCatalogue.Remove(key, expected);

    // The host's cell index is the server's (ServerItemManager.WorldIndex.cs); these are for the
    // Shared code that changes where an item is.
    internal void RegisterHostWorldItem(NetworkedItem item) => ServerPart?.RegisterHostWorldItem(item);

    internal void RefreshHostSpatialPosition(NetworkedItem item, Vector3 absolutePosition,
        ItemWorldParentKind parentKind) => ServerPart?.RefreshHostSpatialPosition(item, absolutePosition, parentKind);

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
