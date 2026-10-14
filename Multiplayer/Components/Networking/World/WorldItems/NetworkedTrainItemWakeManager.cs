using DV;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Core.Items;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Packets.Serverbound;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Components.Networking.World.WorldItems;

internal enum TrainItemWakeReason : byte
{
    SupportRemoved,
    ItemImpact,
    HeavyAcceleration,
    HeavyBraking,
    TrainJolt,
    ViolentRotation,
    Derailment,
    DebugRequest
}

/// <summary>
/// Train-only host policy around the existing spatial lease manager. Settled membership and
/// support evidence are canonical host records; Unity collision evidence may be supplied by the
/// current simulator, but only the host can turn it into a new spatial epoch.
/// </summary>
internal sealed class NetworkedTrainItemWakeManager
{
    internal const int MaximumWakeItems = 32;
    internal const int MaximumWakeDepth = 8;
    internal const float ImpactRelativeSpeedThreshold = 1.25f;
    internal const float ImpactImpulseThreshold = 0.5f;
    internal const float HeavyAccelerationThreshold = 3.5f;
    internal const float HeavyBrakingThreshold = 3f;
    internal const float JoltThreshold = 14f;
    internal const float AngularAccelerationThreshold = 1.25f;
    internal const float MotionSampleInterval = 0.1f;
    internal const float WholeCarWakeCooldown = 1f;
    internal const float WitnessDedupeSeconds = 0.25f;
    internal const float PendingWakeSeconds = 10f;

    private sealed class SettledRecord
    {
        public ushort ItemNetId;
        public uint AuthorityRevision;
        public ushort TrainCarNetId;
        public Vector3 ParentLocalPosition;
        public Quaternion ParentLocalRotation;
        public Bounds ParentLocalBounds;
        public uint SpatialEpoch;
        public float SettledAt;
    }

    private sealed class CarMotion
    {
        public bool Initialized;
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Acceleration;
        public Vector3 AngularVelocity;
        public bool Derailed;
        public float SampledAt;
        public float LastWholeCarWakeAt = -100f;
    }

    private sealed class PendingWake
    {
        public ushort ItemNetId;
        public byte PreferredSimulatorPlayerId;
        public Vector3 InitialLocalVelocity;
        public TrainItemWakeReason Reason;
        public float CreatedAt;
        public float NextAttemptAt;
    }

    private readonly NetworkedItemManager owner;
    private readonly NetworkedItemSpatialManager spatial;
    private readonly Dictionary<ushort, SettledRecord> settledByItem = new();
    private readonly Dictionary<ushort, HashSet<ushort>> settledByCar = new();
    private readonly Dictionary<ushort, CarMotion> carMotion = new();
    private readonly Dictionary<ushort, PendingWake> pendingByItem = new();
    private readonly Dictionary<string, float> recentWitnesses = new(StringComparer.Ordinal);
    private readonly Dictionary<byte, uint> lastWitnessIdByPlayer = new();
    private static uint nextWitnessId;

    internal NetworkedTrainItemWakeManager(NetworkedItemManager owner,
        NetworkedItemSpatialManager spatial)
    {
        this.owner = owner;
        this.spatial = spatial;
    }

    internal void Clear()
    {
        settledByItem.Clear();
        settledByCar.Clear();
        carMotion.Clear();
        pendingByItem.Clear();
        recentWitnesses.Clear();
        lastWitnessIdByPlayer.Clear();
    }

    internal void Update()
    {
        if (NetworkLifecycle.Instance?.IsHost() != true)
            return;
        float now = Time.realtimeSinceStartup;
        RetryPending(now);
        SampleOccupiedCars(now);
        if (recentWitnesses.Count > 256)
        {
            foreach (string key in recentWitnesses.Where(pair => now - pair.Value > 2f)
                         .Select(pair => pair.Key).ToArray())
                recentWitnesses.Remove(key);
        }
    }

    internal void ObserveCanonicalItem(NetworkedItem item)
    {
        if (NetworkLifecycle.Instance?.IsHost() != true || item == null || item.NetId == 0 ||
            !AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record) ||
            record.Placement != ItemPlacementKind.TrainInterior ||
            record.WorldParentKind != ItemWorldParentKind.TrainInterior ||
            record.WorldParentNetId == 0 || spatial.TryGetActiveLeaseIdentity(item.NetId,
                out _, out _, out _, out _))
            return;
        ItemSpatialStateData state = new()
        {
            ItemNetId = item.NetId,
            AuthorityRevision = record.Revision,
            SimulationEpoch = 0,
            WorldParentKind = ItemWorldParentKind.TrainInterior,
            WorldParentNetId = record.WorldParentNetId,
            AbsolutePosition = record.Position,
            Rotation = record.Rotation,
            ParentLocalPosition = record.ParentLocalPosition,
            ParentLocalRotation = record.ParentLocalRotation,
            Phase = ItemSpatialPhase.Settled,
            Sleeping = true
        };
        RegisterSettlement(item, state, record, "canonical-observation");
    }

    internal void OnSpatialSettled(NetworkedItem item, ItemSpatialStateData state,
        AuthoritativeItemRegistry.Record record) =>
        RegisterSettlement(item, state, record, "spatial-commit");

    internal void OnAuthoritativeTransition(NetworkedItem item, AuthoritativeItemRegistry.Record record,
        ItemPlacementKind previousPlacement, bool appliesPlacement)
    {
        if (NetworkLifecycle.Instance?.IsHost() != true || item == null || !appliesPlacement)
            return;
        if (previousPlacement == ItemPlacementKind.TrainInterior &&
            settledByItem.TryGetValue(item.NetId, out SettledRecord previous))
        {
            RemoveSettled(item.NetId);
            WakeSupportedItems(previous, preferredSimulatorPlayerId: record?.PlacementPlayerId ?? 0);
        }
    }

    internal void OnItemRemoved(ushort itemNetId)
    {
        if (itemNetId == 0 || !settledByItem.TryGetValue(itemNetId, out SettledRecord record))
            return;
        RemoveSettled(itemNetId);
        if (NetworkLifecycle.Instance?.IsHost() == true)
            WakeSupportedItems(record, 0);
    }

    internal void ObserveCollision(NetworkedItem source, NetworkedItem target, Collision collision)
    {
        if (source == null || target == null || source == target || collision == null ||
            NetworkLifecycle.Instance == null || spatial == null ||
            !spatial.TryGetActiveLeaseIdentity(source.NetId, out uint sourceRevision,
                out uint sourceEpoch, out byte simulator, out ItemSpatialStateData sourceState) ||
            sourceState.WorldParentKind != ItemWorldParentKind.TrainInterior ||
            sourceState.WorldParentNetId == 0 ||
            !spatial.TryGetCommitted(target.NetId, out ItemSpatialStateData targetState) ||
            targetState.WorldParentKind != ItemWorldParentKind.TrainInterior ||
            targetState.WorldParentNetId != sourceState.WorldParentNetId)
            return;

        byte localPlayerId = NetworkLifecycle.Instance.IsHost()
            ? NetworkLifecycle.Instance.Server.SelfId
            : NetworkLifecycle.Instance.Client.PlayerId;
        if (simulator != localPlayerId)
            return;
        Vector3 relativeVelocity = collision.relativeVelocity;
        Vector3 impulse = collision.impulse;
        if (relativeVelocity.magnitude < ImpactRelativeSpeedThreshold &&
            impulse.magnitude < ImpactImpulseThreshold)
            return;
        Vector3 contact = collision.contactCount > 0 ? collision.GetContact(0).point :
            target.transform.position;
        ServerboundItemTrainWakeWitnessPacket packet = new()
        {
            WitnessId = unchecked(++nextWitnessId),
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
        {
            if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(localPlayerId,
                    out ServerPlayer hostPlayer))
                ReceiveImpactWitness(packet, hostPlayer);
        }
        else
            NetworkLifecycle.Instance.Client.SendItemTrainWakeWitness(packet);
    }

    internal void ReceiveImpactWitness(ServerboundItemTrainWakeWitnessPacket packet,
        ServerPlayer sender)
    {
        string rejection = ValidateWitness(packet, sender, out SettledRecord target,
            out ItemSpatialStateData sourceState);
        if (!string.IsNullOrEmpty(rejection))
        {
            Publish("train-item.wake-witness-rejected", packet?.TargetItemNetId ?? 0,
                packet?.TrainCarNetId ?? 0, new()
                {
                    ["reason"] = rejection,
                    ["senderPlayerId"] = sender?.PlayerId ?? 0,
                    ["sourceItemNetId"] = packet?.SourceItemNetId ?? 0,
                    ["witnessId"] = packet?.WitnessId ?? 0
                }, DebugSeverity.Warning);
            return;
        }

        lastWitnessIdByPlayer[sender.PlayerId] = packet.WitnessId;
        string dedupe = $"{sender.PlayerId}:{packet.SourceItemNetId}:{packet.TargetItemNetId}";
        recentWitnesses[dedupe] = Time.realtimeSinceStartup;
        Vector3 localVelocity = Vector3.zero;
        if (NetworkedTrainCar.TryGet(packet.TrainCarNetId, out TrainCar car) && car != null)
        {
            Transform anchor = car.interior ?? car.transform;
            localVelocity = Vector3.ClampMagnitude(
                anchor.InverseTransformDirection(packet.RelativeVelocity) * 0.35f, 4f);
        }
        ushort[] wakeSet = BuildWakeSet(packet.TrainCarNetId,
            new[] { packet.TargetItemNetId });
        Wake(wakeSet, sender.PlayerId, localVelocity, TrainItemWakeReason.ItemImpact);
        Publish("train-item.wake-witness-accepted", target.ItemNetId, target.TrainCarNetId, new()
        {
            ["sourceItemNetId"] = packet.SourceItemNetId,
            ["senderPlayerId"] = sender.PlayerId,
            ["sourceSimulationEpoch"] = sourceState.SimulationEpoch,
            ["relativeSpeed"] = packet.RelativeVelocity.magnitude,
            ["impulse"] = packet.Impulse.magnitude,
            ["wakeCount"] = wakeSet.Length,
            ["witnessId"] = packet.WitnessId
        });
    }

    internal bool ForceWake(ushort itemNetId, TrainItemWakeReason reason,
        Vector3 initialLocalVelocity, out string rejection)
    {
        rejection = string.Empty;
        if (!settledByItem.TryGetValue(itemNetId, out SettledRecord record))
        {
            rejection = "item-not-glued-in-train";
            return false;
        }
        ushort[] wakeSet = BuildWakeSet(record.TrainCarNetId, new[] { itemNetId });
        Wake(wakeSet, 0, initialLocalVelocity, reason);
        return wakeSet.Length > 0;
    }

    internal Dictionary<string, object> Snapshot(ushort itemNetId = 0, ushort carNetId = 0)
    {
        IEnumerable<SettledRecord> records = settledByItem.Values;
        if (itemNetId != 0) records = records.Where(record => record.ItemNetId == itemNetId);
        if (carNetId != 0) records = records.Where(record => record.TrainCarNetId == carNetId);
        SettledRecord[] selected = records.OrderBy(record => record.TrainCarNetId)
            .ThenBy(record => record.ItemNetId).ToArray();
        return new Dictionary<string, object>
        {
            ["settledCount"] = selected.Length,
            ["activeCarCount"] = settledByCar.Count,
            ["pendingWakeCount"] = pendingByItem.Count,
            ["items"] = selected.Select(record => new Dictionary<string, object>
            {
                ["itemNetId"] = record.ItemNetId,
                ["authorityRevision"] = record.AuthorityRevision,
                ["trainCarNetId"] = record.TrainCarNetId,
                ["spatialEpoch"] = record.SpatialEpoch,
                ["localPosition"] = DebugValueSnapshotter.Snapshot(record.ParentLocalPosition),
                ["localBoundsCenter"] = DebugValueSnapshotter.Snapshot(record.ParentLocalBounds.center),
                ["localBoundsSize"] = DebugValueSnapshotter.Snapshot(record.ParentLocalBounds.size)
            }).ToArray()
        };
    }

    internal void AppendDebugState(ushort itemNetId, Dictionary<string, object> state)
    {
        if (state == null) return;
        state["trainWakeSettled"] = settledByItem.TryGetValue(itemNetId,
            out SettledRecord record);
        state["trainWakePending"] = pendingByItem.ContainsKey(itemNetId);
        if (record != null)
        {
            state["trainWakeCarNetId"] = record.TrainCarNetId;
            state["trainWakeSettledRevision"] = record.AuthorityRevision;
        }
    }

    private void RegisterSettlement(NetworkedItem item, ItemSpatialStateData state,
        AuthoritativeItemRegistry.Record record, string source)
    {
        if (item == null || state == null || record == null || item.NetId == 0 ||
            record.Placement != ItemPlacementKind.TrainInterior ||
            state.WorldParentKind != ItemWorldParentKind.TrainInterior ||
            state.WorldParentNetId == 0)
            return;
        RemoveSettled(item.NetId);
        SettledRecord settled = new()
        {
            ItemNetId = item.NetId,
            AuthorityRevision = record.Revision,
            TrainCarNetId = state.WorldParentNetId,
            ParentLocalPosition = state.ParentLocalPosition,
            ParentLocalRotation = state.ParentLocalRotation,
            ParentLocalBounds = CaptureLocalBounds(item, state),
            SpatialEpoch = state.SimulationEpoch,
            SettledAt = Time.realtimeSinceStartup
        };
        settledByItem[item.NetId] = settled;
        if (!settledByCar.TryGetValue(settled.TrainCarNetId, out HashSet<ushort> items))
            settledByCar[settled.TrainCarNetId] = items = new HashSet<ushort>();
        items.Add(item.NetId);
        pendingByItem.Remove(item.NetId);
        Rigidbody body = item.Item?.ItemRigidbody;
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.Sleep();
            body.isKinematic = true;
        }
        Publish("train-item.settlement-registered", item.NetId, settled.TrainCarNetId, new()
        {
            ["source"] = source,
            ["authorityRevision"] = settled.AuthorityRevision,
            ["simulationEpoch"] = settled.SpatialEpoch
        });
    }

    private void WakeSupportedItems(SettledRecord removed, byte preferredSimulatorPlayerId)
    {
        if (removed == null) return;
        TrainItemWakeNode[] nodes = NodesForCar(removed.TrainCarNetId);
        ushort[] wakeSet = TrainItemWakePlanner.FromRemovedSupport(nodes,
            removed.TrainCarNetId, CoreBounds(removed.ParentLocalBounds), MaximumWakeItems,
            MaximumWakeDepth);
        Wake(wakeSet, preferredSimulatorPlayerId, Vector3.zero,
            TrainItemWakeReason.SupportRemoved);
        Publish("train-item.support-removed", removed.ItemNetId, removed.TrainCarNetId, new()
        {
            ["wakeCount"] = wakeSet.Length,
            ["wakeItems"] = wakeSet.Select(value => (object)value).ToArray()
        });
    }

    private ushort[] BuildWakeSet(ushort carNetId, IEnumerable<ushort> seeds) =>
        TrainItemWakePlanner.FromSeeds(NodesForCar(carNetId), carNetId, seeds,
            MaximumWakeItems, MaximumWakeDepth);

    private TrainItemWakeNode[] NodesForCar(ushort carNetId)
    {
        if (!settledByCar.TryGetValue(carNetId, out HashSet<ushort> ids))
            return Array.Empty<TrainItemWakeNode>();
        return ids.Select(id => settledByItem.TryGetValue(id, out SettledRecord record)
                ? new TrainItemWakeNode(id, carNetId, true, CoreBounds(record.ParentLocalBounds))
                : default)
            .Where(node => node.ItemNetId != 0).ToArray();
    }

    private void Wake(IEnumerable<ushort> itemNetIds, byte preferredSimulatorPlayerId,
        Vector3 initialLocalVelocity, TrainItemWakeReason reason)
    {
        byte selectedSimulator = preferredSimulatorPlayerId;
        foreach (ushort itemNetId in (itemNetIds ?? Array.Empty<ushort>()).Distinct()
                     .Take(MaximumWakeItems).ToArray())
        {
            if (!settledByItem.ContainsKey(itemNetId))
                continue;
            if (spatial.TryBeginTrainWake(itemNetId, selectedSimulator, initialLocalVelocity,
                    reason.ToString(), out byte simulator, out string rejection))
            {
                selectedSimulator = simulator;
                SettledRecord record = settledByItem[itemNetId];
                RemoveSettled(itemNetId);
                pendingByItem.Remove(itemNetId);
                Publish("train-item.wake-granted", itemNetId, record.TrainCarNetId, new()
                {
                    ["reason"] = reason.ToString(),
                    ["simulatorPlayerId"] = simulator
                });
                continue;
            }
            if (rejection == "no-eligible-train-item-simulator" ||
                rejection == "item-representation-unavailable")
            {
                pendingByItem[itemNetId] = new PendingWake
                {
                    ItemNetId = itemNetId,
                    PreferredSimulatorPlayerId = selectedSimulator,
                    InitialLocalVelocity = initialLocalVelocity,
                    Reason = reason,
                    CreatedAt = Time.realtimeSinceStartup,
                    NextAttemptAt = Time.realtimeSinceStartup + 0.5f
                };
                Publish("train-item.wake-pending", itemNetId,
                    settledByItem[itemNetId].TrainCarNetId, new()
                    {
                        ["reason"] = reason.ToString(),
                        ["rejection"] = rejection
                    }, DebugSeverity.Warning);
            }
        }
    }

    private void RetryPending(float now)
    {
        foreach (PendingWake pending in pendingByItem.Values.ToArray())
        {
            if (!settledByItem.ContainsKey(pending.ItemNetId))
            {
                pendingByItem.Remove(pending.ItemNetId);
                continue;
            }
            if (now - pending.CreatedAt >= PendingWakeSeconds)
            {
                pendingByItem.Remove(pending.ItemNetId);
                Publish("train-item.wake-expired", pending.ItemNetId,
                    settledByItem[pending.ItemNetId].TrainCarNetId, new()
                    {
                        ["reason"] = pending.Reason.ToString()
                    }, DebugSeverity.Warning);
                continue;
            }
            if (now < pending.NextAttemptAt)
                continue;
            pending.NextAttemptAt = now + 0.5f;
            Wake(new[] { pending.ItemNetId }, pending.PreferredSimulatorPlayerId,
                pending.InitialLocalVelocity, pending.Reason);
        }
    }

    private void SampleOccupiedCars(float now)
    {
        foreach (ushort carNetId in settledByCar.Keys.ToArray())
        {
            if (!settledByCar.TryGetValue(carNetId, out HashSet<ushort> items) || items.Count == 0)
            {
                settledByCar.Remove(carNetId);
                carMotion.Remove(carNetId);
                continue;
            }
            if (!NetworkedTrainCar.TryGet(carNetId, out TrainCar car) || car?.rb == null)
                continue;
            if (!carMotion.TryGetValue(carNetId, out CarMotion motion))
                carMotion[carNetId] = motion = new CarMotion();
            if (motion.Initialized && now - motion.SampledAt < MotionSampleInterval)
                continue;
            Vector3 position = car.transform.position - WorldMover.currentMove;
            Vector3 velocity = car.rb.velocity;
            Vector3 angularVelocity = car.rb.angularVelocity;
            bool derailed = car.Bogies != null && car.Bogies.Any(bogie => bogie != null && bogie.HasDerailed);
            if (!motion.Initialized)
            {
                InitializeMotion(motion, position, velocity, angularVelocity, derailed, now);
                continue;
            }
            float dt = now - motion.SampledAt;
            float expectedTravel = Mathf.Max(5f, (motion.Velocity.magnitude + velocity.magnitude) * dt * 2f + 3f);
            if (dt <= 0f || dt > 0.75f || Vector3.Distance(position, motion.Position) > expectedTravel)
            {
                InitializeMotion(motion, position, velocity, angularVelocity, derailed, now);
                Publish("train-item.motion-baseline-reset", 0, carNetId, new()
                {
                    ["reason"] = "teleport-or-network-correction"
                });
                continue;
            }
            Vector3 acceleration = (velocity - motion.Velocity) / dt;
            Vector3 jolt = (acceleration - motion.Acceleration) / dt;
            Vector3 angularAcceleration = (angularVelocity - motion.AngularVelocity) / dt;
            TrainItemWakeReason? reason = null;
            if (derailed && !motion.Derailed)
                reason = TrainItemWakeReason.Derailment;
            else if (angularAcceleration.magnitude >= AngularAccelerationThreshold)
                reason = TrainItemWakeReason.ViolentRotation;
            else if (jolt.magnitude >= JoltThreshold && acceleration.magnitude >= 1.5f)
                reason = TrainItemWakeReason.TrainJolt;
            else if (acceleration.magnitude >= HeavyAccelerationThreshold)
                reason = Vector3.Dot(acceleration, motion.Velocity) < 0f &&
                    motion.Velocity.magnitude > 1f
                    ? TrainItemWakeReason.HeavyBraking
                    : TrainItemWakeReason.HeavyAcceleration;
            else if (Vector3.Dot(acceleration, motion.Velocity.normalized) <= -HeavyBrakingThreshold &&
                     motion.Velocity.magnitude > 1f)
                reason = TrainItemWakeReason.HeavyBraking;

            motion.Position = position;
            motion.Velocity = velocity;
            motion.Acceleration = acceleration;
            motion.AngularVelocity = angularVelocity;
            motion.Derailed = derailed;
            motion.SampledAt = now;
            if (!reason.HasValue || now - motion.LastWholeCarWakeAt < WholeCarWakeCooldown)
                continue;
            motion.LastWholeCarWakeAt = now;
            Vector3 localKick = (car.interior ?? car.transform).InverseTransformDirection(-acceleration) * 0.05f;
            ushort[] wakeSet = items.Where(settledByItem.ContainsKey).Take(MaximumWakeItems).ToArray();
            Wake(wakeSet, 0, Vector3.ClampMagnitude(localKick, 1.5f), reason.Value);
            Publish("train-item.car-motion-wake", 0, carNetId, new()
            {
                ["reason"] = reason.Value.ToString(),
                ["acceleration"] = DebugValueSnapshotter.Snapshot(acceleration),
                ["jolt"] = DebugValueSnapshotter.Snapshot(jolt),
                ["angularAcceleration"] = DebugValueSnapshotter.Snapshot(angularAcceleration),
                ["wakeCount"] = wakeSet.Length
            });
        }
    }

    private string ValidateWitness(ServerboundItemTrainWakeWitnessPacket packet,
        ServerPlayer sender, out SettledRecord target, out ItemSpatialStateData sourceState)
    {
        target = null;
        sourceState = null;
        if (packet == null || sender == null || packet.WitnessId == 0 ||
            packet.SourceItemNetId == 0 || packet.TargetItemNetId == 0 ||
            packet.SourceItemNetId == packet.TargetItemNetId || packet.TrainCarNetId == 0)
            return "invalid-witness";
        if (lastWitnessIdByPlayer.TryGetValue(sender.PlayerId, out uint lastWitnessId) &&
            packet.WitnessId <= lastWitnessId)
            return "stale-witness-id";
        if (!Finite(packet.RelativeVelocity) || !Finite(packet.Impulse) ||
            !Finite(packet.AbsoluteContactPoint) || packet.RelativeVelocity.magnitude > 50f ||
            packet.Impulse.magnitude > 100f)
            return "invalid-witness-motion";
        if (packet.RelativeVelocity.magnitude < ImpactRelativeSpeedThreshold &&
            packet.Impulse.magnitude < ImpactImpulseThreshold)
            return "impact-below-threshold";
        string dedupe = $"{sender.PlayerId}:{packet.SourceItemNetId}:{packet.TargetItemNetId}";
        if (recentWitnesses.TryGetValue(dedupe, out float observedAt) &&
            Time.realtimeSinceStartup - observedAt < WitnessDedupeSeconds)
            return "duplicate-impact-witness";
        if (!spatial.TryGetActiveLeaseIdentity(packet.SourceItemNetId,
                out uint sourceRevision, out uint sourceEpoch, out byte simulator, out sourceState) ||
            simulator != sender.PlayerId || sourceRevision != packet.SourceAuthorityRevision ||
            sourceEpoch != packet.SourceSimulationEpoch)
            return "source-not-current-simulator";
        if (sourceState.WorldParentKind != ItemWorldParentKind.TrainInterior ||
            sourceState.WorldParentNetId != packet.TrainCarNetId)
            return "source-not-in-witness-train";
        if (!settledByItem.TryGetValue(packet.TargetItemNetId, out target) ||
            target.AuthorityRevision != packet.TargetAuthorityRevision ||
            target.TrainCarNetId != packet.TrainCarNetId)
            return "target-not-current-settled-train-item";
        if (!AuthoritativeItemRegistry.TryGet(packet.SourceItemNetId,
                out AuthoritativeItemRegistry.Record sourceRecord) ||
            sourceRecord.Placement != ItemPlacementKind.TrainInterior ||
            sourceRecord.WorldParentNetId != packet.TrainCarNetId)
            return "source-canonical-parent-mismatch";
        if (NetworkedTrainCar.TryGet(packet.TrainCarNetId, out TrainCar car) && car != null)
        {
            Vector3 expected = (car.interior ?? car.transform).TransformPoint(target.ParentLocalPosition) -
                               WorldMover.currentMove;
            if ((packet.AbsoluteContactPoint - expected).sqrMagnitude > 16f)
                return "contact-outside-target-envelope";
        }
        return string.Empty;
    }

    private void RemoveSettled(ushort itemNetId)
    {
        if (!settledByItem.TryGetValue(itemNetId, out SettledRecord record))
            return;
        settledByItem.Remove(itemNetId);
        if (settledByCar.TryGetValue(record.TrainCarNetId, out HashSet<ushort> items))
        {
            items.Remove(itemNetId);
            if (items.Count == 0)
            {
                settledByCar.Remove(record.TrainCarNetId);
                carMotion.Remove(record.TrainCarNetId);
            }
        }
    }

    private static Bounds CaptureLocalBounds(NetworkedItem item, ItemSpatialStateData state)
    {
        Transform anchor = null;
        if (NetworkedTrainCar.TryGet(state.WorldParentNetId, out TrainCar car) && car != null)
            anchor = car.interior ?? car.transform;
        if (anchor == null)
            return new Bounds(state.ParentLocalPosition, Vector3.one * 0.35f);
        Collider[] colliders = item.GetComponentsInChildren<Collider>(true)
            .Where(collider => collider != null && !collider.isTrigger).ToArray();
        if (colliders.Length == 0)
            return new Bounds(state.ParentLocalPosition, Vector3.one * 0.35f);
        bool initialized = false;
        Bounds local = default;
        foreach (Collider collider in colliders)
        {
            Bounds world = collider.bounds;
            Vector3 min = world.min;
            Vector3 max = world.max;
            for (int mask = 0; mask < 8; mask++)
            {
                Vector3 point = new((mask & 1) == 0 ? min.x : max.x,
                    (mask & 2) == 0 ? min.y : max.y,
                    (mask & 4) == 0 ? min.z : max.z);
                Vector3 localPoint = anchor.InverseTransformPoint(point);
                if (!initialized)
                {
                    local = new Bounds(localPoint, Vector3.zero);
                    initialized = true;
                }
                else
                    local.Encapsulate(localPoint);
            }
        }
        if (!initialized || local.size.sqrMagnitude < 0.000001f)
            return new Bounds(state.ParentLocalPosition, Vector3.one * 0.35f);
        return local;
    }

    private static TrainItemWakeBounds CoreBounds(Bounds bounds) => new(bounds.center.x,
        bounds.center.y, bounds.center.z, bounds.extents.x, bounds.extents.y,
        bounds.extents.z);

    private static void InitializeMotion(CarMotion motion, Vector3 position, Vector3 velocity,
        Vector3 angularVelocity, bool derailed, float now)
    {
        motion.Initialized = true;
        motion.Position = position;
        motion.Velocity = velocity;
        motion.Acceleration = Vector3.zero;
        motion.AngularVelocity = angularVelocity;
        motion.Derailed = derailed;
        motion.SampledAt = now;
    }

    private static bool Finite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) &&
        IsFinite(value.z);

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static void Publish(string eventName, ushort itemNetId, ushort carNetId,
        Dictionary<string, object> data, DebugSeverity severity = DebugSeverity.Info) =>
        DebugRuntime.Publish("train-item-wake", eventName, DebugRuntimeSide.Server, severity,
            itemNetId == 0 ? "TrainCar" : "Item",
            (itemNetId == 0 ? carNetId : itemNetId).ToString(), data: new(data ?? new())
            {
                ["trainCarNetId"] = carNetId
            });
}
