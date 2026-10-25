using DV;
using DV.CabControls;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Shared.Items.Physics;

/// <summary>
/// Host-controlled transient Rigidbody simulation. Logical authority remains in
/// LiveItems; this manager orders motion with a separate epoch/sequence stream and
/// commits only a reliable final pose to the durable authority revision. This is what every game
/// has: the leases it knows of, simulating its own and following the others'. The host grants,
/// transfers and commits leases in ServerItemSpatial; a remote client receives them, and sends
/// its samples, in ClientItemSpatial.
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
    /// <summary>A lease renewed because its flight changed parent: a new epoch at a new revision, same simulator (OI-PHY-05).</summary>
    internal const string ParentChangeLeaseReason = "parent-change";
    private const float ObserverSnapDistance = 6f;
    private const float ObserverInterpolationRate = 10f;
    private const float ObserverPredictionLimit = 0.15f;

    internal sealed class Lease
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
        // The parent its samples show and since when, for committing a change of parent mid-flight (OI-PHY-05).
        public string SampledParent;
        public float SampledParentSince;
        public ItemSpatialStateData LastAccepted;
        public ItemSpatialStateData Target;
    }


    private readonly Dictionary<ushort, Lease> leases = new();
    private readonly Dictionary<ushort, ItemSpatialStateData> committedStates = new();
    private readonly Dictionary<ushort, uint> epochs = new();
    // Why each item's last lease ended, on the host (a commit, a revoke): what scenarios and the
    // debug snapshot read to tell an explicit end from one noticed later (OI-PHY-02).
    private readonly Dictionary<ushort, string> leaseEnds = new();
    // The host's side (ServerItemSpatial) grants, transfers and commits leases in these.
    internal Dictionary<ushort, Lease> Leases => leases;
    internal Dictionary<ushort, ItemSpatialStateData> CommittedStates => committedStates;
    internal Dictionary<ushort, uint> Epochs => epochs;
    internal Dictionary<ushort, string> LeaseEnds => leaseEnds;

    /// <summary>Why the item's last lease ended on the host, or empty while none has.</summary>
    internal string LastLeaseEnd(ushort itemNetId) =>
        leaseEnds.TryGetValue(itemNetId, out string reason) ? reason : string.Empty;

    internal void Clear()
    {
        foreach (Lease lease in leases.Values.ToArray())
            RestoreLocalBodyAfterLease(lease);
        leases.Clear();
        committedStates.Clear();
        epochs.Clear();
        leaseEnds.Clear();
    }

    internal bool TryGetLatestAccepted(ushort itemNetId, out ItemSpatialStateData state)
    {
        state = null;
        if (!leases.TryGetValue(itemNetId, out Lease lease) || lease.LastAccepted == null)
            return false;
        state = lease.LastAccepted.Clone();
        return true;
    }

    /// <summary>
    /// A train item this game got already settled (in a Create, or a convergence correction) stays
    /// kinematic with its car, as on every game that saw it settle, and its settled state is this
    /// game's committed one, so its simulator can report an impact on it (OI-PHY-06, -12, F34).
    /// One with a lease open is left to the lease.
    /// </summary>
    internal void HoldSettledTrainItem(NetworkedItem item, ItemUpdate snapshot)
    {
        if (item == null || snapshot == null || leases.ContainsKey(item.NetId))
            return;
        Rigidbody body = item.Item?.ItemRigidbody;
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.Sleep();
            body.isKinematic = true;
        }
        committedStates[item.NetId] = new ItemSpatialStateData
        {
            ItemNetId = item.NetId,
            AuthorityRevision = snapshot.AuthorityRevision,
            WorldParentKind = ItemWorldParentKind.TrainInterior,
            WorldParentNetId = snapshot.WorldParentNetId,
            AbsolutePosition = snapshot.ItemPosition,
            Rotation = snapshot.ItemRotation,
            ParentLocalPosition = snapshot.ParentLocalPosition,
            ParentLocalRotation = snapshot.ParentLocalRotation,
            Phase = ItemSpatialPhase.Settled,
            Sleeping = true
        };
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

    // Another player simulates this item: our copy is a kinematic proxy following their samples.
    internal bool IsSimulatedRemotely(ushort itemNetId) =>
        leases.TryGetValue(itemNetId, out Lease lease) &&
        lease.SimulatorPlayerId != InventoryIntegration.LocalPlayerId;

    internal bool TryGetActiveLeaseIdentity(ushort itemNetId, out uint authorityRevision,
        out uint simulationEpoch, out byte simulatorPlayerId, out ItemSpatialStateData state)
    {
        authorityRevision = 0;
        simulationEpoch = 0;
        simulatorPlayerId = 0;
        state = null;
        if (!leases.TryGetValue(itemNetId, out Lease lease) || lease.LastAccepted == null)
            return false;
        authorityRevision = lease.AuthorityRevision;
        simulationEpoch = lease.Epoch;
        simulatorPlayerId = lease.SimulatorPlayerId;
        state = lease.LastAccepted.Clone();
        return true;
    }

    internal void ApplyLatestAcceptedToSnapshot(ItemUpdate snapshot)
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

    /// <summary>
    /// The local game simulates this lease: a sample of the body to send, when one is due, and
    /// whether it settled (then it's the lease's last). The host takes its own samples itself
    /// (ServerItemSpatial); a client sends them.
    /// </summary>
    internal bool TrySampleLocalLease(NetworkedItem item, Lease lease, bool host,
        out ItemSpatialStateData state, out bool settled)
    {
        state = null;
        settled = false;
        Rigidbody body = item.Item?.ItemRigidbody;
        if (body == null || body.isKinematic || lease.SettlementSent)
            return false;
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
        settled = now - lease.StartedAt >= MinimumEpochSeconds &&
                       (sleeping || (lease.LowMotionSince >= 0f &&
                            now - lease.LowMotionSince >= SettlementDwellSeconds) || stableTrainPose);
        if (!settled && now - lease.LastSentAt < interval)
            return false;
        if (settled)
            AdoptRestingShelf(item);

        state = Capture(item, lease.AuthorityRevision, lease.Epoch,
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
            lease.SettlementSent = true;
        return true;
    }

    /// <summary>
    /// An item coming to rest on a shelf before vanilla has parented it there: vanilla does on its
    /// next collision with the shelf (<c>ItemReparentingBase.OnCollisionEnter</c>), about eight frames
    /// after a drop, and a dropped mug can sleep sooner. Its settlement was then the world's, and the
    /// parenting that followed this game's alone (SH1). The shelf under it is taken first, as vanilla
    /// would take it: the first thing under the item, if that is part of a shelf.
    /// </summary>
    private static void AdoptRestingShelf(NetworkedItem item)
    {
        if (item.GetComponentInParent<ItemStaticParent>() != null ||
            item.TryGetPhysicalTrainParent(out TrainCar car) && car != null)
            return;
        Collider[] own = item.GetComponentsInChildren<Collider>();
        Bounds bounds = new(item.transform.position, Vector3.zero);
        foreach (Collider collider in own)
            if (collider != null && !collider.isTrigger)
                bounds.Encapsulate(collider.bounds);
        RaycastHit[] hits = UnityEngine.Physics.RaycastAll(bounds.center, Vector3.down, bounds.extents.y + ShelfReach, ~0,
            QueryTriggerInteraction.Ignore);
        foreach (RaycastHit hit in hits.OrderBy(hit => hit.distance))
        {
            if (hit.collider == null || own.Contains(hit.collider))
                continue;
            ItemStaticParent shelf = hit.collider.GetComponentInParent<ItemStaticParent>();
            if (shelf != null)
                item.GetComponent<ItemReparentingBase>()?.ParentItemExternal(shelf.transform, null, shelf);
            return;
        }
    }

    // How far below an item's colliders a shelf it rests on may be, m.
    private const float ShelfReach = 0.15f;

    internal static void ApplyObservedPose(NetworkedItem item, Lease lease, float now)
    {
        if (item == null || lease?.Target == null)
            return;
        ItemSpatialStateData predicted = lease.Target.Clone();
        float prediction = Mathf.Clamp(now - lease.LastAcceptedAt, 0f, ObserverPredictionLimit);
        if (prediction > 0f && !predicted.Sleeping && predicted.Phase != ItemSpatialPhase.Settled)
        {
            if (predicted.WorldParentKind == ItemWorldParentKind.World)
            {
                predicted.AbsolutePosition += predicted.LinearVelocity * prediction;
                // A sample just before it lands would carry it on into the ground: the simulator's
                // body stops on the surface (or its terrain guard puts it back there).
                if (NetworkedItemManager.BelowTerrain(predicted.AbsolutePosition + WorldMover.currentMove, out _) > 0f &&
                    !(NetworkedItemManager.BelowTerrain(lease.Target.AbsolutePosition + WorldMover.currentMove, out _) > 0f))
                    predicted.AbsolutePosition = lease.Target.AbsolutePosition;
            }
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

    internal static ItemSpatialStateData Capture(NetworkedItem item, uint revision, uint epoch,
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

    internal static void ApplyPose(NetworkedItem item, ItemSpatialStateData state, bool immediate)
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
            WorldItemStaticParentRegistry.TryResolve(state.WorldParentKey, state.AbsolutePosition, out Transform parent))
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

    internal static void ConfigureLeasePresentation(NetworkedItem item, bool localSimulator,
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

    internal static void RestoreLocalBodyAfterLease(Lease lease)
    {
        if (lease == null || !NetworkedItem.TryGet(lease.ItemNetId, out NetworkedItem item) || item == null)
            return;
        Rigidbody body = item.Item?.ItemRigidbody;
        if (body == null) return;
        body.velocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        body.Sleep();
    }

    internal static void SetProxyKinematic(NetworkedItem item, bool kinematic)
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

    internal static void ApplySimulationVelocity(NetworkedItem item, ItemSpatialStateData state)
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

    internal static void ApplyCommitMetadata(NetworkedItem item, ItemSpatialStateData state)
    {
        item.ApplyAuthorityMetadata(new ItemUpdate
        {
            ItemNetId = item.NetId,
            AuthorityRevision = state.AuthorityRevision,
            Relation = item.Relation,
            InventoryClaimSlot = item.InventoryClaimSlot,
            InventoryClaimFlags = item.InventoryClaimFlags,
            TransitionReason = ItemTransitionReason.SpatialSettlement
        });
    }

    internal static bool Finite(ItemSpatialStateData state) =>
        Finite(state.AbsolutePosition) && Finite(state.Rotation) && Finite(state.LinearVelocity) &&
        Finite(state.AngularVelocity) && Finite(state.ParentLocalPosition) && Finite(state.ParentLocalRotation);

    internal static bool Finite(Vector3 value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

    internal static bool Finite(Quaternion value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    internal static Dictionary<string, object> SpatialDebug(ItemSpatialStateData state) => new()
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

    internal static void Publish(string eventName, ushort itemNetId, Dictionary<string, object> data,
        DebugRuntimeSide side = DebugRuntimeSide.Server, bool highFrequency = false) =>
        DebugRuntime.Publish("item-spatial", eventName, side, entityType: "Item",
            entityId: itemNetId.ToString(), data: data, highFrequency: highFrequency);

    internal void AppendDebugState(ushort itemNetId, Dictionary<string, object> state)
    {
        if (state != null && leaseEnds.TryGetValue(itemNetId, out string ended))
            state["spatialLastLeaseEnd"] = ended;
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
