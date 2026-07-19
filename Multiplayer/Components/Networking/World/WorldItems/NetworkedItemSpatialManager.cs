using DV;
using DV.CabControls;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Core.Items;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Components.Networking.World.WorldItems;

/// <summary>
/// Host-controlled transient Rigidbody simulation. Logical authority remains in
/// AuthoritativeItemRegistry; this manager orders motion with a separate epoch/sequence stream and
/// commits only a reliable final pose to the durable authority revision.
/// </summary>
internal sealed class NetworkedItemSpatialManager
{
    internal const float FastSampleInterval = 1f / 15f;
    internal const float SlowSampleInterval = 1f / 8f;
    internal const float LowLinearSpeed = 0.05f;
    internal const float LowAngularSpeed = 0.1f;
    internal const float SettlementDwellSeconds = 0.5f;
    internal const float TrainLocalSettlementDwellSeconds = 1f;
    internal const float TrainLocalSettlementPositionTolerance = 0.01f;
    internal const float TrainLocalSettlementRotationToleranceDegrees = 3f;
    internal const float MinimumEpochSeconds = 0.25f;
    internal const float SampleTimeoutSeconds = 3f;
    internal const float UnresolvedMotionWarningSeconds = 30f;
    internal const float MaximumLinearSpeed = 150f;
    internal const float MaximumAngularSpeed = 100f;
    internal const float MaximumSampleDisplacementPerSecond = 180f;
    internal const float MaximumSimulatorDistance = 192f;
    private const float ObserverSnapDistance = 6f;
    private const float ObserverInterpolationRate = 10f;
    private const float ObserverPredictionLimit = 0.15f;

    private sealed class Lease
    {
        public ushort ItemNetId;
        public uint AuthorityRevision;
        public uint Epoch;
        public byte SimulatorPlayerId;
        public uint LastSequence;
        public uint NextLocalSequence = 1;
        public float StartedAt;
        public float LastAcceptedAt;
        public float LastSentAt;
        public float LowMotionSince = -1f;
        public float StableTrainPoseSince = -1f;
        public Vector3 StableTrainLocalPosition;
        public Quaternion StableTrainLocalRotation = Quaternion.identity;
        public bool HasStableTrainPose;
        public bool SettlementSent;
        public bool UnresolvedMotionWarningEmitted;
        public ItemSpatialStateData LastAccepted;
        public ItemSpatialStateData Target;
    }

    private readonly NetworkedItemManager owner;
    private readonly Dictionary<ushort, Lease> leases = new();
    private readonly Dictionary<ushort, ItemSpatialStateData> committedStates = new();
    private readonly Dictionary<ushort, uint> epochs = new();
    private readonly Dictionary<ushort, ClientboundItemSpatialLeasePacket> pendingClientLeases = new();
    private readonly Dictionary<ushort, (NetworkedItem Item, uint Revision, byte Simulator,
        ItemSpatialPhase Phase)> pendingHostBegins = new();

    internal NetworkedItemSpatialManager(NetworkedItemManager owner)
    {
        this.owner = owner;
    }

    internal void Clear()
    {
        foreach (Lease lease in leases.Values.ToArray())
            RestoreLocalBodyAfterLease(lease);
        leases.Clear();
        committedStates.Clear();
        epochs.Clear();
        pendingClientLeases.Clear();
        pendingHostBegins.Clear();
    }

    internal void Update()
    {
        if (NetworkLifecycle.Instance == null)
            return;
        ProcessPendingClientLeases();
        if (NetworkLifecycle.Instance.IsHost())
        {
            ProcessPendingHostBegins();
            UpdateHostLeases();
        }
        else if (NetworkLifecycle.Instance.IsClientRunning)
            UpdateClientLeases();
    }

    internal void OnAuthoritativeTransitionCommitted(NetworkedItem item, ItemUpdateData snapshot,
        AuthoritativeItemRegistry.Record record, ServerPlayer actor, ItemPlacementKind previousPlacement,
        bool appliesPlacement)
    {
        if (!NetworkLifecycle.Instance.IsHost() || item == null || record == null || !appliesPlacement)
            return;

        bool entersWorld = snapshot.ItemState is ItemState.Dropped or ItemState.Thrown &&
                           record.Placement is ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
                               ItemPlacementKind.StaticParent;
        bool releasedByPlayer = previousPlacement is ItemPlacementKind.PlayerHand or
            ItemPlacementKind.PlayerInventory || snapshot.ItemState == ItemState.Thrown;
        if (entersWorld && releasedByPlayer && actor != null)
        {
            pendingHostBegins[item.NetId] = (item, record.Revision, actor.PlayerId,
                snapshot.ItemState == ItemState.Thrown ? ItemSpatialPhase.InFlight : ItemSpatialPhase.Sliding);
            return;
        }

        if (!entersWorld)
        {
            pendingHostBegins.Remove(item.NetId);
            RevokeHostLease(item.NetId, "logical-placement-changed");
            committedStates.Remove(item.NetId);
        }
    }

    internal bool ResetAuthoredItemToBaseline(NetworkedItem item,
        AuthoritativeItemRegistry.Record record)
    {
        if (!NetworkLifecycle.Instance.IsHost() || item == null || record == null ||
            item.NetId == 0 || record.NetId != item.NetId ||
            !WorldItemStableIdentity.IsValid(record.AuthoredItemKey))
            return false;

        RevokeHostLease(item.NetId, "authored-slot-reset", notify: true);
        pendingHostBegins.Remove(item.NetId);
        uint epoch = epochs.TryGetValue(item.NetId, out uint previous) ? previous + 1 : 1;
        epochs[item.NetId] = epoch;
        ItemSpatialStateData state = new()
        {
            ItemNetId = item.NetId,
            AuthorityRevision = record.Revision,
            SimulationEpoch = epoch,
            SampleSequence = 0,
            SourceTick = NetworkLifecycle.Instance.Tick,
            SimulatorPlayerId = 0,
            Phase = ItemSpatialPhase.Settled,
            AbsolutePosition = record.BaselinePosition,
            Rotation = record.BaselineRotation,
            LinearVelocity = Vector3.zero,
            AngularVelocity = Vector3.zero,
            WorldParentKind = ItemWorldParentKind.World,
            ParentLocalRotation = Quaternion.identity,
            Sleeping = true
        };
        if (!AuthoritativeItemRegistry.TryCommitSpatialPose(item, record.Revision, state,
                out AuthoritativeItemRegistry.Record committed, out string rejection))
        {
            Publish("item.authored-slot-reset-rejected", item.NetId, new()
            {
                ["reason"] = rejection ?? string.Empty,
                ["authoredItemKey"] = record.AuthoredItemKey
            });
            return false;
        }

        state.AuthorityRevision = committed.Revision;
        committed.HasPersistentOverride = false;
        committedStates[item.NetId] = state.Clone();
        ApplyPose(item, state, immediate: true);
        ApplyCommitMetadata(item, state);
        owner.RefreshHostSpatialPosition(item, state.AbsolutePosition, state.WorldParentKind);
        Rigidbody body = item.Item?.ItemRigidbody;
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.Sleep();
        }
        NetworkLifecycle.Instance.Server.SendItemSpatialCommit(item,
            new ClientboundItemSpatialCommitPacket { State = state });
        return true;
    }

    internal void OnPlayerDisconnected(byte playerId)
    {
        if (!NetworkLifecycle.Instance.IsHost() || playerId == 0)
            return;
        foreach (Lease lease in leases.Values.Where(value => value.SimulatorPlayerId == playerId).ToArray())
        {
            if (!NetworkedItem.TryGet(lease.ItemNetId, out NetworkedItem item) || item == null)
            {
                RevokeHostLease(lease.ItemNetId, "simulator-disconnected");
                continue;
            }
            if (!TryTransferLease(item, lease, "simulator-disconnected"))
                CommitHostSettlement(item, lease, lease.LastAccepted, "simulator-disconnected");
        }
    }

    private void ProcessPendingHostBegins()
    {
        foreach (var pair in pendingHostBegins.ToArray())
        {
            pendingHostBegins.Remove(pair.Key);
            var pending = pair.Value;
            if (pending.Item == null || !AuthoritativeItemRegistry.TryGet(pair.Key, out var record) ||
                record.Revision != pending.Revision ||
                record.Placement is not (ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
                    ItemPlacementKind.StaticParent))
                continue;
            BeginHostLease(pending.Item, record, pending.Simulator, pending.Phase);
        }
    }

    private void BeginHostLease(NetworkedItem item, AuthoritativeItemRegistry.Record record,
        byte simulatorPlayerId, ItemSpatialPhase phase)
    {
        if (item == null || record == null || simulatorPlayerId == 0)
            return;
        RevokeHostLease(item.NetId, "superseded", notify: false);
        uint epoch = epochs.TryGetValue(item.NetId, out uint previous) ? previous + 1 : 1;
        epochs[item.NetId] = epoch;
        ItemSpatialStateData baseline = Capture(item, record.Revision, epoch, 0, simulatorPlayerId, phase);
        float now = Time.realtimeSinceStartup;
        Lease lease = new()
        {
            ItemNetId = item.NetId,
            AuthorityRevision = record.Revision,
            Epoch = epoch,
            SimulatorPlayerId = simulatorPlayerId,
            StartedAt = now,
            LastAcceptedAt = now,
            LastAccepted = baseline.Clone(),
            Target = baseline.Clone()
        };
        leases[item.NetId] = lease;

        bool localSimulator = simulatorPlayerId == NetworkLifecycle.Instance.Server.SelfId;
        ConfigureLeasePresentation(item, localSimulator, baseline);
        NetworkLifecycle.Instance.Server.SendItemSpatialLease(item, new ClientboundItemSpatialLeasePacket
        {
            Active = true,
            Reason = "world-release",
            State = baseline
        });
        Publish("item.spatial-lease-granted", item.NetId, new()
        {
            ["simulationEpoch"] = epoch,
            ["simulatorPlayerId"] = simulatorPlayerId,
            ["authorityRevision"] = record.Revision,
            ["phase"] = phase.ToString(),
            ["worldParentKind"] = baseline.WorldParentKind.ToString()
        });
    }

    private void RevokeHostLease(ushort itemNetId, string reason, bool notify = true)
    {
        if (!leases.TryGetValue(itemNetId, out Lease lease))
            return;
        leases.Remove(itemNetId);
        RestoreLocalBodyAfterLease(lease);
        if (notify && NetworkedItem.TryGet(itemNetId, out NetworkedItem item) && item != null)
            NetworkLifecycle.Instance.Server.SendItemSpatialLease(item, new ClientboundItemSpatialLeasePacket
            {
                Active = false,
                Reason = reason ?? string.Empty,
                State = lease.LastAccepted?.Clone()
            });
        Publish("item.spatial-lease-revoked", itemNetId, new()
        {
            ["simulationEpoch"] = lease.Epoch,
            ["simulatorPlayerId"] = lease.SimulatorPlayerId,
            ["reason"] = reason ?? string.Empty
        });
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
        bool localSimulator = state.SimulatorPlayerId == NetworkLifecycle.Instance.Client.PlayerId;
        ConfigureLeasePresentation(item, localSimulator, state);
        if (localSimulator && packet.Reason?.StartsWith("lease-transfer:", StringComparison.Ordinal) == true)
            ApplySimulationVelocity(item, state);
        Publish("item.spatial-lease-applied", item.NetId, new()
        {
            ["simulationEpoch"] = state.SimulationEpoch,
            ["simulatorPlayerId"] = state.SimulatorPlayerId,
            ["localSimulator"] = localSimulator
        }, DebugRuntimeSide.Client);
    }

    internal void ReceiveClientSample(ItemSpatialStateData state, ServerPlayer sender)
    {
        if (!TryAcceptHostSample(state, sender, settlement: false, out Lease lease, out NetworkedItem item,
                out string rejection))
        {
            Reject(state, sender, rejection);
            TerminateInvalidCurrentLease(state, sender, rejection);
            return;
        }
        ApplyAcceptedHostSample(item, lease, state);
        NetworkLifecycle.Instance.Server.SendItemSpatialSample(item,
            new ClientboundItemSpatialSamplePacket { State = state.Clone() }, sender.PlayerId);
        Publish("item.spatial-sample-relayed", item.NetId, SpatialDebug(state), highFrequency: true);
    }

    internal void ReceiveClientSettlement(ItemSpatialStateData state, ServerPlayer sender)
    {
        if (!TryAcceptHostSample(state, sender, settlement: true, out Lease lease, out NetworkedItem item,
                out string rejection))
        {
            Reject(state, sender, rejection);
            TerminateInvalidCurrentLease(state, sender, rejection);
            return;
        }
        ApplyAcceptedHostSample(item, lease, state);
        CommitHostSettlement(item, lease, state, "client-settlement");
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
            state.SimulatorPlayerId != NetworkLifecycle.Instance.Client.PlayerId)
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
        Rigidbody body = item.Item?.ItemRigidbody;
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.Sleep();
            if (state.WorldParentKind == ItemWorldParentKind.TrainInterior ||
                state.SimulatorPlayerId != NetworkLifecycle.Instance.Client.PlayerId)
                body.isKinematic = true;
        }
        Publish("item.spatial-commit-applied", item.NetId, SpatialDebug(state), DebugRuntimeSide.Client);
    }

    internal bool TryGetLatestAccepted(ushort itemNetId, out ItemSpatialStateData state)
    {
        state = null;
        if (!leases.TryGetValue(itemNetId, out Lease lease) || lease.LastAccepted == null)
            return false;
        state = lease.LastAccepted.Clone();
        return true;
    }

    internal bool TryGetCommitted(ushort itemNetId, out ItemSpatialStateData state)
    {
        state = null;
        if (!committedStates.TryGetValue(itemNetId, out ItemSpatialStateData committed) ||
            committed == null)
            return false;
        state = committed.Clone();
        return true;
    }

    internal void ApplyLatestAcceptedToSnapshot(ItemUpdateData snapshot)
    {
        if (snapshot == null || !TryGetLatestAccepted(snapshot.ItemNetId, out ItemSpatialStateData state))
            return;
        snapshot.ItemPosition = state.AbsolutePosition;
        snapshot.ItemRotation = state.Rotation;
        snapshot.WorldParentKind = state.WorldParentKind;
        snapshot.WorldParentNetId = state.WorldParentNetId;
        snapshot.WorldParentKey = state.WorldParentKey ?? string.Empty;
        snapshot.ParentLocalPosition = state.ParentLocalPosition;
        snapshot.ParentLocalRotation = state.ParentLocalRotation;
    }

    internal void SendActiveLeasesTo(ServerPlayer player, IEnumerable<ItemUpdateData> snapshots)
    {
        if (player?.Peer == null || snapshots == null)
            return;
        foreach (ushort itemNetId in snapshots.Where(snapshot => snapshot != null &&
                     snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create))
                 .Select(snapshot => snapshot.ItemNetId).Distinct())
        {
            if (!leases.TryGetValue(itemNetId, out Lease lease) || lease.LastAccepted == null ||
                !NetworkedItem.TryGet(itemNetId, out NetworkedItem item) || item == null)
                continue;
            NetworkLifecycle.Instance.Server.SendItemSpatialLease(player,
                new ClientboundItemSpatialLeasePacket
                {
                    Active = true,
                    Reason = "interest-entry",
                    State = lease.LastAccepted.Clone()
                });
        }
    }

    private void UpdateHostLeases()
    {
        float now = Time.realtimeSinceStartup;
        byte hostId = NetworkLifecycle.Instance.Server.SelfId;
        foreach (Lease lease in leases.Values.ToArray())
        {
            if (!NetworkedItem.TryGet(lease.ItemNetId, out NetworkedItem item) || item?.Item == null)
            {
                RevokeHostLease(lease.ItemNetId, "representation-missing");
                continue;
            }
            if (lease.SimulatorPlayerId == hostId)
                SampleLocalLease(item, lease, host: true);
            else
            {
                if (lease.Target != null)
                    ApplyObservedPose(item, lease, now);
                if (now - lease.LastAcceptedAt < SampleTimeoutSeconds)
                    continue;
                Publish("item.spatial-timeout", item.NetId, new()
                {
                    ["simulationEpoch"] = lease.Epoch,
                    ["simulatorPlayerId"] = lease.SimulatorPlayerId,
                    ["lastSampleAgeSeconds"] = now - lease.LastAcceptedAt
                });
                if (!TryTransferLease(item, lease, "sample-timeout"))
                    CommitHostSettlement(item, lease, lease.LastAccepted, "sample-timeout");
            }
        }
    }

    private bool TryTransferLease(NetworkedItem item, Lease previous, string reason)
    {
        if (item == null || previous?.LastAccepted == null ||
            !AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record) ||
            record.Revision != previous.AuthorityRevision)
            return false;

        ServerPlayer candidate = NetworkLifecycle.Instance.Server.ServerPlayers
            .Where(player => player != null && player.PlayerId != previous.SimulatorPlayerId &&
                             player.LoadingState >= PlayerLoadingState.ReadyForItems &&
                             (player.AbsoluteWorldPosition - previous.LastAccepted.AbsolutePosition).sqrMagnitude <=
                             MaximumSimulatorDistance * MaximumSimulatorDistance)
            .OrderByDescending(player => player.PlayerId == NetworkLifecycle.Instance.Server.SelfId &&
                                         item.gameObject.activeInHierarchy)
            .ThenBy(player => (player.AbsoluteWorldPosition - previous.LastAccepted.AbsolutePosition).sqrMagnitude)
            .FirstOrDefault(player => player.PlayerId == NetworkLifecycle.Instance.Server.SelfId
                ? item.gameObject.activeInHierarchy
                : player.KnownItems.ContainsKey(item) && player.AcknowledgedWorldItems.Contains(item.NetId));
        if (candidate == null)
            return false;

        leases.Remove(item.NetId);
        uint epoch = epochs.TryGetValue(item.NetId, out uint lastEpoch)
            ? lastEpoch + 1
            : previous.Epoch + 1;
        epochs[item.NetId] = epoch;
        ItemSpatialStateData baseline = previous.LastAccepted.Clone();
        baseline.AuthorityRevision = record.Revision;
        baseline.SimulationEpoch = epoch;
        baseline.SampleSequence = 0;
        baseline.SourceTick = NetworkLifecycle.Instance.Tick;
        baseline.SimulatorPlayerId = candidate.PlayerId;
        float now = Time.realtimeSinceStartup;
        Lease transferred = new()
        {
            ItemNetId = item.NetId,
            AuthorityRevision = record.Revision,
            Epoch = epoch,
            SimulatorPlayerId = candidate.PlayerId,
            StartedAt = now,
            LastAcceptedAt = now,
            LastAccepted = baseline.Clone(),
            Target = baseline.Clone()
        };
        leases[item.NetId] = transferred;
        bool localHost = candidate.PlayerId == NetworkLifecycle.Instance.Server.SelfId;
        ConfigureLeasePresentation(item, localHost, baseline);
        if (localHost)
            ApplySimulationVelocity(item, baseline);
        NetworkLifecycle.Instance.Server.SendItemSpatialLease(item,
            new ClientboundItemSpatialLeasePacket
            {
                Active = true,
                Reason = $"lease-transfer:{reason}",
                State = baseline
            });
        Publish("item.spatial-lease-transferred", item.NetId, new()
        {
            ["previousSimulationEpoch"] = previous.Epoch,
            ["simulationEpoch"] = epoch,
            ["previousSimulatorPlayerId"] = previous.SimulatorPlayerId,
            ["simulatorPlayerId"] = candidate.PlayerId,
            ["reason"] = reason ?? string.Empty
        });
        return true;
    }

    private void UpdateClientLeases()
    {
        byte localId = NetworkLifecycle.Instance.Client.PlayerId;
        foreach (Lease lease in leases.Values.ToArray())
        {
            if (!NetworkedItem.TryGet(lease.ItemNetId, out NetworkedItem item) || item?.Item == null)
                continue;
            if (lease.SimulatorPlayerId == localId)
                SampleLocalLease(item, lease, host: false);
            else if (lease.Target != null)
                ApplyObservedPose(item, lease, Time.realtimeSinceStartup);
        }
    }

    private void SampleLocalLease(NetworkedItem item, Lease lease, bool host)
    {
        Rigidbody body = item.Item?.ItemRigidbody;
        if (body == null || body.isKinematic || lease.SettlementSent)
            return;
        float now = Time.realtimeSinceStartup;
        if (!lease.UnresolvedMotionWarningEmitted &&
            now - lease.StartedAt >= UnresolvedMotionWarningSeconds)
        {
            lease.UnresolvedMotionWarningEmitted = true;
            Publish("item.spatial-unresolved-motion", item.NetId, new()
            {
                ["simulationEpoch"] = lease.Epoch,
                ["simulatorPlayerId"] = lease.SimulatorPlayerId,
                ["activeSeconds"] = now - lease.StartedAt,
                ["linearSpeed"] = body.velocity.magnitude,
                ["angularSpeed"] = body.angularVelocity.magnitude,
                ["networkPolicy"] = "observe-only"
            }, host ? DebugRuntimeSide.Server : DebugRuntimeSide.Client);
        }
        float linearSpeed = RelativeLinearVelocity(item, body).magnitude;
        float angularSpeed = RelativeAngularVelocity(item, body).magnitude;
        float interval = linearSpeed > 1f || angularSpeed > 1f ? FastSampleInterval : SlowSampleInterval;
        bool sleeping = body.IsSleeping();
        bool lowMotion = linearSpeed <= LowLinearSpeed && angularSpeed <= LowAngularSpeed;
        lease.LowMotionSince = lowMotion ? (lease.LowMotionSince < 0f ? now : lease.LowMotionSince) : -1f;
        bool stableTrainPose = linearSpeed <= LowLinearSpeed &&
                               HasStableTrainLocalPose(item, lease, now);
        bool settled = now - lease.StartedAt >= MinimumEpochSeconds &&
                       (sleeping || (lease.LowMotionSince >= 0f &&
                            now - lease.LowMotionSince >= SettlementDwellSeconds) || stableTrainPose);
        if (!settled && now - lease.LastSentAt < interval)
            return;

        ItemSpatialStateData state = Capture(item, lease.AuthorityRevision, lease.Epoch,
            lease.NextLocalSequence++, lease.SimulatorPlayerId,
            settled ? ItemSpatialPhase.Settled : linearSpeed > 1f ? ItemSpatialPhase.InFlight : ItemSpatialPhase.Sliding);
        state.Sleeping = sleeping || settled;
        lease.LastSentAt = now;
        Dictionary<string, object> debug = SpatialDebug(state);
        if (settled)
            debug["settlementBasis"] = sleeping ? "rigidbody-sleep" : stableTrainPose
                ? "stable-train-local-pose" : "low-motion-dwell";
        Publish(settled ? "item.spatial-settlement-proposed" : "item.spatial-sample-sent",
            item.NetId, debug, host ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            highFrequency: !settled);
        if (settled)
        {
            lease.SettlementSent = true;
            if (host)
            {
                ServerPlayer actor = null;
                string rejection = "unknown-host-simulator";
                if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(lease.SimulatorPlayerId, out actor) &&
                    TryAcceptHostSample(state, actor, settlement: true, out _, out _, out rejection))
                {
                    ApplyAcceptedHostSample(item, lease, state);
                    CommitHostSettlement(item, lease, state, "host-settlement");
                }
                else
                    Reject(state, actor, rejection ?? "host-settlement-rejected");
            }
            else
                NetworkLifecycle.Instance.Client.SendItemSpatialSettlement(state);
            return;
        }

        if (host)
        {
            ServerPlayer actor = null;
            string rejection = "unknown-host-simulator";
            if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(lease.SimulatorPlayerId, out actor) &&
                TryAcceptHostSample(state, actor, settlement: false, out _, out _, out rejection))
            {
                ApplyAcceptedHostSample(item, lease, state);
                NetworkLifecycle.Instance.Server.SendItemSpatialSample(item,
                    new ClientboundItemSpatialSamplePacket { State = state.Clone() }, lease.SimulatorPlayerId);
                Publish("item.spatial-sample-relayed", item.NetId, SpatialDebug(state), highFrequency: true);
            }
            else
                Reject(state, actor, rejection ?? "host-sample-rejected");
        }
        else
            NetworkLifecycle.Instance.Client.SendItemSpatialSample(state);
    }

    private bool TryAcceptHostSample(ItemSpatialStateData state, ServerPlayer sender, bool settlement,
        out Lease lease, out NetworkedItem item, out string rejection)
    {
        lease = null;
        item = null;
        rejection = string.Empty;
        if (state == null || sender == null || !leases.TryGetValue(state?.ItemNetId ?? 0, out lease) ||
            !NetworkedItem.TryGet(state?.ItemNetId ?? 0, out item) || item == null)
        {
            rejection = "unknown-item-or-lease";
            return false;
        }
        bool registryCurrent = AuthoritativeItemRegistry.TryGet(state.ItemNetId,
            out AuthoritativeItemRegistry.Record record) && record.Revision == lease.AuthorityRevision;
        bool simulatablePlacement = registryCurrent && record.Placement is ItemPlacementKind.World or
            ItemPlacementKind.TrainInterior or ItemPlacementKind.StaticParent;
        rejection = ItemSpatialStreamValidator.Validate(
            new ItemSpatialLeaseToken(lease.SimulatorPlayerId, lease.AuthorityRevision, lease.Epoch,
                lease.LastSequence, simulatablePlacement),
            new ItemSpatialSampleToken(sender.PlayerId, state.SimulatorPlayerId, state.AuthorityRevision,
                state.SimulationEpoch, state.SampleSequence));
        if (!string.IsNullOrEmpty(rejection))
        {
            return false;
        }
        if (!registryCurrent)
        {
            rejection = "authority-registry-mismatch";
            return false;
        }
        float rotationMagnitude = Mathf.Sqrt(state.Rotation.x * state.Rotation.x +
                                             state.Rotation.y * state.Rotation.y +
                                             state.Rotation.z * state.Rotation.z +
                                             state.Rotation.w * state.Rotation.w);
        if (!Finite(state) || rotationMagnitude < 0.5f || rotationMagnitude > 1.5f ||
            state.LinearVelocity.magnitude > MaximumLinearSpeed ||
            state.AngularVelocity.magnitude > MaximumAngularSpeed)
        {
            rejection = "invalid-or-excessive-motion";
            return false;
        }
        if ((state.AbsolutePosition - sender.AbsoluteWorldPosition).sqrMagnitude >
            MaximumSimulatorDistance * MaximumSimulatorDistance)
        {
            rejection = "item-outside-simulator-range";
            return false;
        }
        float elapsed = Mathf.Max(0.016f, Time.realtimeSinceStartup - lease.LastAcceptedAt);
        float allowed = MaximumSampleDisplacementPerSecond * elapsed + 3f;
        bool sameParent = state.WorldParentKind == lease.LastAccepted.WorldParentKind &&
                          state.WorldParentNetId == lease.LastAccepted.WorldParentNetId &&
                          string.Equals(state.WorldParentKey ?? string.Empty,
                              lease.LastAccepted.WorldParentKey ?? string.Empty, StringComparison.Ordinal);
        Vector3 displacement = sameParent && state.WorldParentKind != ItemWorldParentKind.World
            ? state.ParentLocalPosition - lease.LastAccepted.ParentLocalPosition
            : state.AbsolutePosition - lease.LastAccepted.AbsolutePosition;
        if (displacement.sqrMagnitude > allowed * allowed)
        {
            rejection = "sample-displacement-envelope-exceeded";
            return false;
        }
        if (!ValidateParent(state, sender))
        {
            rejection = "invalid-spatial-parent";
            return false;
        }
        if (settlement && state.Phase != ItemSpatialPhase.Settled)
        {
            rejection = "settlement-phase-required";
            return false;
        }
        return true;
    }

    private void TerminateInvalidCurrentLease(ItemSpatialStateData state, ServerPlayer sender,
        string rejection)
    {
        if (state == null || sender == null ||
            rejection is "stale-sample-sequence" or "stale-simulation-epoch" or
                "stale-spatial-authority-revision" or "sender-not-simulator" ||
            !leases.TryGetValue(state.ItemNetId, out Lease lease) ||
            lease.SimulatorPlayerId != sender.PlayerId || lease.Epoch != state.SimulationEpoch ||
            lease.LastAccepted == null ||
            !NetworkedItem.TryGet(state.ItemNetId, out NetworkedItem item) || item == null)
            return;
        CommitHostSettlement(item, lease, lease.LastAccepted, $"sample-rejected:{rejection}");
        Publish("item.spatial-reconciled", item.NetId, new()
        {
            ["simulationEpoch"] = lease.Epoch,
            ["simulatorPlayerId"] = lease.SimulatorPlayerId,
            ["reason"] = rejection ?? string.Empty,
            ["reconciledTo"] = DebugValueSnapshotter.Snapshot(lease.LastAccepted.AbsolutePosition)
        });
    }

    private static bool ValidateParent(ItemSpatialStateData state, ServerPlayer sender)
    {
        if (state.WorldParentKind == ItemWorldParentKind.World)
            return true;
        if (state.WorldParentKind == ItemWorldParentKind.TrainInterior)
            return NetworkedTrainCar.TryGet(state.WorldParentNetId, out TrainCar car) && car != null &&
                   (car.transform.position - WorldMover.currentMove - sender.AbsoluteWorldPosition).sqrMagnitude <=
                   MaximumSimulatorDistance * MaximumSimulatorDistance;
        return state.WorldParentKind == ItemWorldParentKind.StaticParent &&
               WorldItemStaticParentRegistry.TryResolve(state.WorldParentKey, out Transform parent) && parent != null &&
               (parent.position - WorldMover.currentMove - sender.AbsoluteWorldPosition).sqrMagnitude <=
               MaximumSimulatorDistance * MaximumSimulatorDistance;
    }

    private void ApplyAcceptedHostSample(NetworkedItem item, Lease lease, ItemSpatialStateData state)
    {
        lease.LastSequence = state.SampleSequence;
        lease.LastAcceptedAt = Time.realtimeSinceStartup;
        lease.LastAccepted = state.Clone();
        lease.Target = state.Clone();
        if (lease.SimulatorPlayerId == NetworkLifecycle.Instance.Server.SelfId)
            ApplyPose(item, state, immediate: true);
        else
            SetProxyKinematic(item, true);
        owner.RefreshHostSpatialPosition(item, state.AbsolutePosition, state.WorldParentKind);
        Publish("item.spatial-sample-accepted", item.NetId, SpatialDebug(state), highFrequency: true);
    }

    private static void ApplyObservedPose(NetworkedItem item, Lease lease, float now)
    {
        if (item == null || lease?.Target == null)
            return;
        ItemSpatialStateData predicted = lease.Target.Clone();
        float prediction = Mathf.Clamp(now - lease.LastAcceptedAt, 0f, ObserverPredictionLimit);
        if (prediction > 0f && !predicted.Sleeping && predicted.Phase != ItemSpatialPhase.Settled)
        {
            if (predicted.WorldParentKind == ItemWorldParentKind.World)
                predicted.AbsolutePosition += predicted.LinearVelocity * prediction;
            else
                predicted.ParentLocalPosition += predicted.LinearVelocity * prediction;

            float angularSpeed = predicted.AngularVelocity.magnitude;
            if (angularSpeed > 0.0001f)
            {
                Quaternion delta = Quaternion.AngleAxis(angularSpeed * Mathf.Rad2Deg * prediction,
                    predicted.AngularVelocity / angularSpeed);
                if (predicted.WorldParentKind == ItemWorldParentKind.World)
                    predicted.Rotation = delta * predicted.Rotation;
                else
                    predicted.ParentLocalRotation = delta * predicted.ParentLocalRotation;
            }
        }
        ApplyPose(item, predicted, immediate: false);
    }

    private void CommitHostSettlement(NetworkedItem item, Lease lease, ItemSpatialStateData state, string reason)
    {
        if (item == null || state == null || !leases.TryGetValue(item.NetId, out Lease current) ||
            current.Epoch != lease.Epoch)
            return;
        if (!AuthoritativeItemRegistry.TryCommitSpatialPose(item, lease.AuthorityRevision, state,
                out AuthoritativeItemRegistry.Record record, out string rejection))
        {
            Reject(state, null, rejection);
            return;
        }
        state = state.Clone();
        state.AuthorityRevision = record.Revision;
        state.Phase = ItemSpatialPhase.Settled;
        state.LinearVelocity = Vector3.zero;
        state.AngularVelocity = Vector3.zero;
        state.Sleeping = true;
        committedStates[item.NetId] = state.Clone();
        ApplyPose(item, state, immediate: true);
        ApplyCommitMetadata(item, state);
        owner.RefreshHostSpatialPosition(item, state.AbsolutePosition, state.WorldParentKind);
        leases.Remove(item.NetId);
        Rigidbody body = item.Item?.ItemRigidbody;
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.Sleep();
            if (state.WorldParentKind == ItemWorldParentKind.TrainInterior ||
                lease.SimulatorPlayerId != NetworkLifecycle.Instance.Server.SelfId)
                body.isKinematic = true;
        }
        NetworkLifecycle.Instance.Server.SendItemSpatialCommit(item,
            new ClientboundItemSpatialCommitPacket { State = state });
        Publish("item.spatial-commit-accepted", item.NetId, new(SpatialDebug(state))
        {
            ["reason"] = reason ?? string.Empty
        });
    }

    private static ItemSpatialStateData Capture(NetworkedItem item, uint revision, uint epoch,
        uint sequence, byte simulator, ItemSpatialPhase phase)
    {
        Rigidbody body = item.Item?.ItemRigidbody;
        Vector3 absolute = item.transform.position - WorldMover.currentMove;
        ItemSpatialStateData state = new()
        {
            ItemNetId = item.NetId,
            AuthorityRevision = revision,
            SimulationEpoch = epoch,
            SampleSequence = sequence,
            SourceTick = NetworkLifecycle.Instance.Tick,
            SimulatorPlayerId = simulator,
            Phase = phase,
            AbsolutePosition = absolute,
            Rotation = item.transform.rotation,
            LinearVelocity = body?.velocity ?? Vector3.zero,
            AngularVelocity = body?.angularVelocity ?? Vector3.zero,
            WorldParentKind = ItemWorldParentKind.World,
            ParentLocalRotation = Quaternion.identity,
            Sleeping = body == null || body.IsSleeping()
        };
        item.TryGetPhysicalTrainParent(out TrainCar car);
        ItemStaticParent staticParent = item.GetComponentInParent<ItemStaticParent>();
        Transform anchor = null;
        Rigidbody anchorBody = null;
        if (car != null)
        {
            state.WorldParentKind = ItemWorldParentKind.TrainInterior;
            state.WorldParentNetId = car.GetNetId();
            anchor = car.interior ?? car.transform;
            anchorBody = car.rb;
        }
        else if (staticParent != null)
        {
            state.WorldParentKind = ItemWorldParentKind.StaticParent;
            state.WorldParentKey = WorldItemStableIdentity.CaptureStaticParent(staticParent.transform);
            anchor = staticParent.transform;
            anchorBody = staticParent.GetComponent<Rigidbody>();
        }
        if (anchor != null)
        {
            state.ParentLocalPosition = anchor.InverseTransformPoint(item.transform.position);
            state.ParentLocalRotation = Quaternion.Inverse(anchor.rotation) * item.transform.rotation;
            // CabItemRigidbody already represents residual motion relative to its receiving train.
            // Subtracting TrainCar.rb here charges the train's movement against the item and makes
            // a stationary cab item look as if it is moving backward at train speed forever.
            Vector3 linearVelocity = body?.velocity ?? Vector3.zero;
            Vector3 angularVelocity = body?.angularVelocity ?? Vector3.zero;
            if (car == null)
            {
                linearVelocity -= anchorBody?.velocity ?? Vector3.zero;
                angularVelocity -= anchorBody?.angularVelocity ?? Vector3.zero;
            }
            state.LinearVelocity = anchor.InverseTransformDirection(linearVelocity);
            state.AngularVelocity = anchor.InverseTransformDirection(angularVelocity);
        }
        return state;
    }

    private static Vector3 RelativeLinearVelocity(NetworkedItem item, Rigidbody body)
    {
        item.TryGetPhysicalTrainParent(out TrainCar car);
        if (car == null) return body.velocity;
        Transform anchor = car.interior ?? car.transform;
        return anchor.InverseTransformDirection(body.velocity);
    }

    private static Vector3 RelativeAngularVelocity(NetworkedItem item, Rigidbody body)
    {
        item.TryGetPhysicalTrainParent(out TrainCar car);
        if (car == null) return body.angularVelocity;
        Transform anchor = car.interior ?? car.transform;
        return anchor.InverseTransformDirection(body.angularVelocity);
    }

    private static bool HasStableTrainLocalPose(NetworkedItem item, Lease lease, float now)
    {
        if (item == null || lease == null ||
            !item.TryGetPhysicalTrainParent(out TrainCar car) || car == null)
        {
            if (lease != null)
            {
                lease.HasStableTrainPose = false;
                lease.StableTrainPoseSince = -1f;
            }
            return false;
        }

        Transform anchor = car.interior ?? car.transform;
        Vector3 localPosition = anchor.InverseTransformPoint(item.transform.position);
        Quaternion localRotation = Quaternion.Inverse(anchor.rotation) * item.transform.rotation;
        bool outsideTolerance = !lease.HasStableTrainPose ||
            Vector3.Distance(lease.StableTrainLocalPosition, localPosition) >
                TrainLocalSettlementPositionTolerance ||
            Quaternion.Angle(lease.StableTrainLocalRotation, localRotation) >
                TrainLocalSettlementRotationToleranceDegrees;
        if (outsideTolerance)
        {
            lease.HasStableTrainPose = true;
            lease.StableTrainPoseSince = now;
            lease.StableTrainLocalPosition = localPosition;
            lease.StableTrainLocalRotation = localRotation;
            return false;
        }

        return lease.StableTrainPoseSince >= 0f &&
               now - lease.StableTrainPoseSince >= TrainLocalSettlementDwellSeconds;
    }

    private static void ApplyPose(NetworkedItem item, ItemSpatialStateData state, bool immediate)
    {
        if (item == null || state == null)
            return;
        Transform anchor = ResolveAnchor(state);
        Vector3 targetPosition = anchor == null
            ? state.AbsolutePosition + WorldMover.currentMove
            : anchor.TransformPoint(state.ParentLocalPosition);
        Quaternion targetRotation = anchor == null
            ? state.Rotation
            : anchor.rotation * state.ParentLocalRotation;
        ApplyAuthoritativeParent(item, state, anchor);
        float distance = Vector3.Distance(item.transform.position, targetPosition);
        if (immediate || distance >= ObserverSnapDistance)
        {
            item.transform.SetPositionAndRotation(targetPosition, targetRotation);
            return;
        }
        float t = 1f - Mathf.Exp(-ObserverInterpolationRate * Time.unscaledDeltaTime);
        item.transform.position = Vector3.Lerp(item.transform.position, targetPosition, t);
        item.transform.rotation = Quaternion.Slerp(item.transform.rotation, targetRotation, t);
    }

    private static Transform ResolveAnchor(ItemSpatialStateData state)
    {
        if (state.WorldParentKind == ItemWorldParentKind.TrainInterior &&
            NetworkedTrainCar.TryGet(state.WorldParentNetId, out TrainCar car))
            return car.interior ?? car.transform;
        if (state.WorldParentKind == ItemWorldParentKind.StaticParent &&
            WorldItemStaticParentRegistry.TryResolve(state.WorldParentKey, out Transform parent))
            return parent;
        return null;
    }

    private static void ApplyAuthoritativeParent(NetworkedItem item, ItemSpatialStateData state,
        Transform anchor)
    {
        Transform desired = anchor ?? WorldMover.OriginShiftParent;
        ItemReparentingBase reparenting = item.GetComponent<ItemReparentingBase>();
        if (item.transform.parent == desired && (reparenting == null || reparenting.CurrentParent == desired))
            return;

        if (reparenting == null)
        {
            item.transform.SetParent(desired, true);
            return;
        }

        if (state.WorldParentKind == ItemWorldParentKind.TrainInterior &&
            NetworkedTrainCar.TryGet(state.WorldParentNetId, out TrainCar car))
        {
            reparenting.ParentItemExternal(desired, car.rb, null);
            return;
        }

        ItemStaticParent staticParent = state.WorldParentKind == ItemWorldParentKind.StaticParent
            ? desired.GetComponent<ItemStaticParent>()
            : null;
        reparenting.ParentItemExternal(desired, null, staticParent);
    }

    private static void ConfigureLeasePresentation(NetworkedItem item, bool localSimulator,
        ItemSpatialStateData baseline)
    {
        if (item == null) return;
        if (!localSimulator)
        {
            ApplyPose(item, baseline, immediate: true);
            SetProxyKinematic(item, true);
        }
        else
            SetProxyKinematic(item, false);
    }

    private static void RestoreLocalBodyAfterLease(Lease lease)
    {
        if (lease == null || !NetworkedItem.TryGet(lease.ItemNetId, out NetworkedItem item) || item == null)
            return;
        Rigidbody body = item.Item?.ItemRigidbody;
        if (body == null) return;
        body.velocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        body.Sleep();
    }

    private static void SetProxyKinematic(NetworkedItem item, bool kinematic)
    {
        Rigidbody body = item?.Item?.ItemRigidbody;
        if (body == null) return;
        if (kinematic)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        body.isKinematic = kinematic;
    }

    private static void ApplySimulationVelocity(NetworkedItem item, ItemSpatialStateData state)
    {
        Rigidbody body = item?.Item?.ItemRigidbody;
        if (body == null || state == null) return;
        Transform anchor = ResolveAnchor(state);
        Rigidbody anchorBody = null;
        if (state.WorldParentKind == ItemWorldParentKind.TrainInterior &&
            NetworkedTrainCar.TryGet(state.WorldParentNetId, out TrainCar car))
            anchorBody = car.rb;
        else if (anchor != null)
            anchorBody = anchor.GetComponentInParent<Rigidbody>();
        body.isKinematic = false;
        bool trainRelativeBody = state.WorldParentKind == ItemWorldParentKind.TrainInterior;
        body.velocity = anchor == null
            ? state.LinearVelocity
            : anchor.TransformDirection(state.LinearVelocity) +
                (trainRelativeBody ? Vector3.zero : anchorBody?.velocity ?? Vector3.zero);
        body.angularVelocity = anchor == null
            ? state.AngularVelocity
            : anchor.TransformDirection(state.AngularVelocity) +
                (trainRelativeBody ? Vector3.zero : anchorBody?.angularVelocity ?? Vector3.zero);
        body.WakeUp();
    }

    private static void ApplyCommitMetadata(NetworkedItem item, ItemSpatialStateData state)
    {
        item.ApplyAuthorityMetadata(new ItemUpdateData
        {
            ItemNetId = item.NetId,
            AuthorityRevision = state.AuthorityRevision,
            PersistentOwnerPlayerId = item.PersistentOwnerPlayerId,
            InventoryClaimPlayerId = item.InventoryClaimPlayerId,
            InventoryClaimSlot = item.InventoryClaimSlot,
            InventoryClaimFlags = item.InventoryClaimFlags,
            TransitionReason = ItemTransitionReason.SpatialSettlement
        });
    }

    private static bool Finite(ItemSpatialStateData state) =>
        Finite(state.AbsolutePosition) && Finite(state.Rotation) && Finite(state.LinearVelocity) &&
        Finite(state.AngularVelocity) && Finite(state.ParentLocalPosition) && Finite(state.ParentLocalRotation);

    private static bool Finite(Vector3 value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

    private static bool Finite(Quaternion value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static void Reject(ItemSpatialStateData state, ServerPlayer sender, string reason)
    {
        DebugRuntime.Publish("item-spatial", "item.spatial-sample-rejected", DebugRuntimeSide.Server,
            DebugSeverity.Warning, "Item", (state?.ItemNetId ?? 0).ToString(), new()
            {
                ["reason"] = reason ?? string.Empty,
                ["senderPlayerId"] = sender?.PlayerId ?? 0,
                ["simulationEpoch"] = state?.SimulationEpoch ?? 0,
                ["sampleSequence"] = state?.SampleSequence ?? 0
            });
    }

    private static Dictionary<string, object> SpatialDebug(ItemSpatialStateData state) => new()
    {
        ["authorityRevision"] = state.AuthorityRevision,
        ["simulationEpoch"] = state.SimulationEpoch,
        ["sampleSequence"] = state.SampleSequence,
        ["simulatorPlayerId"] = state.SimulatorPlayerId,
        ["phase"] = state.Phase.ToString(),
        ["position"] = DebugValueSnapshotter.Snapshot(state.AbsolutePosition),
        ["linearVelocity"] = DebugValueSnapshotter.Snapshot(state.LinearVelocity),
        ["angularVelocity"] = DebugValueSnapshotter.Snapshot(state.AngularVelocity),
        ["worldParentKind"] = state.WorldParentKind.ToString(),
        ["worldParentNetId"] = state.WorldParentNetId,
        ["sleeping"] = state.Sleeping
    };

    private static void Publish(string eventName, ushort itemNetId, Dictionary<string, object> data,
        DebugRuntimeSide side = DebugRuntimeSide.Server, bool highFrequency = false) =>
        DebugRuntime.Publish("item-spatial", eventName, side, entityType: "Item",
            entityId: itemNetId.ToString(), data: data, highFrequency: highFrequency);

    internal void AppendDebugState(ushort itemNetId, Dictionary<string, object> state)
    {
        if (state == null || !leases.TryGetValue(itemNetId, out Lease lease))
        {
            if (state != null) state["spatialLeaseActive"] = false;
            return;
        }
        state["spatialLeaseActive"] = true;
        state["spatialAuthorityRevision"] = lease.AuthorityRevision;
        state["spatialSimulationEpoch"] = lease.Epoch;
        state["spatialSimulatorPlayerId"] = lease.SimulatorPlayerId;
        state["spatialLastSequence"] = lease.LastSequence;
        state["spatialLastSampleAgeSeconds"] = Time.realtimeSinceStartup - lease.LastAcceptedAt;
        state["spatialActiveSeconds"] = Time.realtimeSinceStartup - lease.StartedAt;
        state["spatialSettlementSent"] = lease.SettlementSent;
        state["spatialUnresolvedMotion"] = lease.UnresolvedMotionWarningEmitted;
        if (lease.LastAccepted != null)
        {
            state["spatialPhase"] = lease.LastAccepted.Phase.ToString();
            state["spatialAcceptedPosition"] = DebugValueSnapshotter.Snapshot(lease.LastAccepted.AbsolutePosition);
            state["spatialLinearVelocity"] = DebugValueSnapshotter.Snapshot(lease.LastAccepted.LinearVelocity);
            state["spatialAngularVelocity"] = DebugValueSnapshotter.Snapshot(lease.LastAccepted.AngularVelocity);
            state["spatialWorldParentKind"] = lease.LastAccepted.WorldParentKind.ToString();
            state["spatialWorldParentNetId"] = lease.LastAccepted.WorldParentNetId;
        }
    }
}
