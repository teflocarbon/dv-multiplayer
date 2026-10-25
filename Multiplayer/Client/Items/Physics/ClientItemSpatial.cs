using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Client.Items.ChangeCauses;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.Physics;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Session;
using UnityEngine;
using static Multiplayer.Shared.Items.Physics.NetworkedItemSpatialManager;

namespace Multiplayer.Client.Items.Physics;

/// <summary>
/// The remote client's side of the spatial stream (<see cref="NetworkedItemSpatialManager"/>): the
/// host's leases, samples and commits as they arrive, sending its own samples and settlement for the
/// leases it simulates, and following the others'. It works on the Shared manager's lease table; the
/// host's side is <c>Server/Items/Physics/ServerItemSpatial</c>.
/// </summary>
internal sealed class ClientItemSpatial
{
    private readonly NetworkedItemSpatialManager spatial;
    private readonly Dictionary<ushort, ClientboundItemSpatialLeasePacket> pendingClientLeases = new();

    internal ClientItemSpatial(NetworkedItemSpatialManager spatial)
    {
        this.spatial = spatial;
    }

    private Dictionary<ushort, Lease> leases => spatial.Leases;
    private Dictionary<ushort, ItemSpatialStateData> committedStates => spatial.CommittedStates;

    internal void Clear() => pendingClientLeases.Clear();

    internal void Update()
    {
        if (NetworkLifecycle.Instance == null)
            return;
        ProcessPendingClientLeases();
        if (NetworkRole.IsRemoteClient)
            UpdateClientLeases();
    }

    internal void ReceiveClientLease(ClientboundItemSpatialLeasePacket packet)
    {
        ItemSpatialStateData state = packet?.State;
        if (state == null || state.ItemNetId == 0)
            return;
        if (!packet.Active)
        {
            if (leases.TryGetValue(state.ItemNetId, out Lease existing) && existing.Epoch <= state.SimulationEpoch)
            {
                leases.Remove(state.ItemNetId);
                RestoreLocalBodyAfterLease(existing);
            }
            return;
        }
        if (!NetworkedItem.TryGet(state.ItemNetId, out NetworkedItem item) || item == null)
        {
            pendingClientLeases[state.ItemNetId] = packet;
            return;
        }
        ApplyClientLease(item, packet);
    }

    private void ProcessPendingClientLeases()
    {
        foreach (KeyValuePair<ushort, ClientboundItemSpatialLeasePacket> pair in pendingClientLeases.ToArray())
        {
            if (!NetworkedItem.TryGet(pair.Key, out NetworkedItem item) || item == null)
                continue;
            pendingClientLeases.Remove(pair.Key);
            ApplyClientLease(item, pair.Value);
        }
    }

    private void ApplyClientLease(NetworkedItem item, ClientboundItemSpatialLeasePacket packet)
    {
        ItemSpatialStateData state = packet.State;
        if (leases.TryGetValue(item.NetId, out Lease old) && old.Epoch > state.SimulationEpoch)
            return;
        // A lease for our own release, arriving once we have the item back (a quick re-grab):
        // the pickup we sent ends it at the host. Simulating it would loosen the held body.
        if (state.SimulatorPlayerId == NetworkLifecycle.Instance.LocalSession.PlayerId && item.IsLocallyPossessed())
        {
            Publish("item.spatial-lease-skipped", item.NetId, new()
            {
                ["simulationEpoch"] = state.SimulationEpoch,
                ["reason"] = "item-locally-possessed"
            }, DebugRuntimeSide.Client);
            return;
        }
        float now = Time.realtimeSinceStartup;
        Lease lease = new()
        {
            ItemNetId = item.NetId,
            AuthorityRevision = state.AuthorityRevision,
            Epoch = state.SimulationEpoch,
            SimulatorPlayerId = state.SimulatorPlayerId,
            LastSequence = state.SampleSequence,
            StartedAt = now,
            LastAcceptedAt = now,
            LastAccepted = state.Clone(),
            Target = state.Clone()
        };
        leases[item.NetId] = lease;
        // A flight that changed parent is at a new revision (OI-PHY-05): this copy's own requests,
        // a grab mid-flight, carry it.
        if (packet.Reason == ParentChangeLeaseReason)
            ApplyCommitMetadata(item, state);
        bool localSimulator = state.SimulatorPlayerId == NetworkLifecycle.Instance.LocalSession.PlayerId;
        ConfigureLeasePresentation(item, localSimulator, state);
        if (localSimulator && (packet.Reason?.StartsWith("lease-transfer:", StringComparison.Ordinal) == true ||
                               packet.Reason?.StartsWith("train-wake:", StringComparison.Ordinal) == true))
            ApplySimulationVelocity(item, state);
        Publish("item.spatial-lease-applied", item.NetId, new()
        {
            ["simulationEpoch"] = state.SimulationEpoch,
            ["simulatorPlayerId"] = state.SimulatorPlayerId,
            ["localSimulator"] = localSimulator
        }, DebugRuntimeSide.Client);
    }

    internal void ReceiveRelayedSample(ItemSpatialStateData state)
    {
        if (state == null || !leases.TryGetValue(state.ItemNetId, out Lease lease) ||
            state.SimulationEpoch != lease.Epoch || state.SampleSequence <= lease.LastSequence)
            return;
        lease.LastSequence = state.SampleSequence;
        lease.LastAcceptedAt = Time.realtimeSinceStartup;
        lease.LastAccepted = state.Clone();
        lease.Target = state.Clone();
        if (NetworkedItem.TryGet(state.ItemNetId, out NetworkedItem item) && item != null &&
            state.SimulatorPlayerId != NetworkLifecycle.Instance.LocalSession.PlayerId)
            SetProxyKinematic(item, true);
    }

    internal void ReceiveSpatialCommit(ClientboundItemSpatialCommitPacket packet)
    {
        ItemSpatialStateData state = packet?.State;
        if (state == null || state.ItemNetId == 0)
            return;
        if (leases.TryGetValue(state.ItemNetId, out Lease lease) && lease.Epoch > state.SimulationEpoch)
            return;
        leases.Remove(state.ItemNetId);
        if (!NetworkedItem.TryGet(state.ItemNetId, out NetworkedItem item) || item == null)
            return;
        ApplyPose(item, state, immediate: true);
        ApplyCommitMetadata(item, state);
        committedStates[state.ItemNetId] = state.Clone();
        if (packet.MoveReason != ItemMoveReason.Unknown)
            ClientItemChangeLog.OnMove(item, new ChangeCause<ItemMoveReason>(packet.MoveReason,
                    packet.MovedByPlayerId, null,
                    NetworkLifecycle.Instance.Tick, packet.MoveDetail),
                NetworkLifecycle.Instance.LocalSession.PlayerId);
        Rigidbody body = item.Item?.ItemRigidbody;
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.Sleep();
            if (state.WorldParentKind == ItemWorldParentKind.TrainInterior ||
                state.SimulatorPlayerId != NetworkLifecycle.Instance.LocalSession.PlayerId)
                body.isKinematic = true;
        }
        Publish("item.spatial-commit-applied", item.NetId, SpatialDebug(state), DebugRuntimeSide.Client);
    }

    private void UpdateClientLeases()
    {
        byte localId = NetworkLifecycle.Instance.LocalSession.PlayerId;
        foreach (Lease lease in leases.Values.ToArray())
        {
            if (!NetworkedItem.TryGet(lease.ItemNetId, out NetworkedItem item) || item?.Item == null)
                continue;
            if (lease.SimulatorPlayerId == localId)
            {
                if (!spatial.TrySampleLocalLease(item, lease, host: false, out ItemSpatialStateData state, out bool settled))
                    continue;
                if (settled)
                    ItemClient.Current.SendItemSpatialSettlement(state);
                else
                    ItemClient.Current.SendItemSpatialSample(state);
            }
            else if (lease.Target != null)
                ApplyObservedPose(item, lease, Time.realtimeSinceStartup);
        }
    }
}
