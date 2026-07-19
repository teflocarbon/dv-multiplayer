#if DEBUG
using Multiplayer.Components.Networking.Train;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests.TrainFixtures;

/// <summary>
/// Cross-peer observation barrier for loose items carried by a moving train. It deliberately
/// observes production item/train state instead of repairing it, so failures preserve the exact
/// parent, lease, and relative-motion disagreement that the scenario is intended to diagnose.
/// </summary>
internal sealed class DebugTrainItemRuntimeDriver
{
    public IEnumerator ObserveMotion(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        ushort itemNetId = RequiredUShort(command, "itemNetId");
        ushort carNetId = RequiredUShort(command, "carNetId");
        float minimumTrainSpeedKph = Mathf.Clamp(OptionalFloat(command,
            "minimumTrainSpeedKph", 0.5f), 0f, 100f);
        float maximumLocalDrift = Mathf.Clamp(OptionalFloat(command,
            "maximumLocalDrift", 0.75f), 0.01f, 10f);
        float maximumRelativeSpeed = Mathf.Clamp(OptionalFloat(command,
            "maximumRelativeSpeed", 2f), 0.01f, 50f);
        float sampleSeconds = Mathf.Clamp(OptionalFloat(command, "sampleSeconds", 1.5f),
            0.25f, 5f);
        float waitSeconds = Mathf.Clamp(OptionalFloat(command, "waitSeconds", 20f),
            1f, 25f);
        bool requireSettled = OptionalBool(command, "requireSettled", true);

        NetworkedItem item = null;
        TrainCar car = null;
        ItemSpatialStateData committed = null;
        float deadline = Time.realtimeSinceStartup + waitSeconds;
        while (Time.realtimeSinceStartup <= deadline)
        {
            NetworkedItem.TryGet(itemNetId, out item);
            NetworkedTrainCar.TryGet(carNetId, out car);
            bool committedAvailable = NetworkedItemManager.Instance != null &&
                NetworkedItemManager.Instance.TryGetCommittedSpatialState(itemNetId,
                    out committed);
            bool activeLease = NetworkedItemManager.Instance?.HasActiveSpatialState(itemNetId) == true;
            if (item != null && car != null && (!requireSettled ||
                (!activeLease && committedAvailable && committed?.Phase == ItemSpatialPhase.Settled)))
                break;
            yield return null;
        }

        if (item == null) throw new InvalidOperationException("train-item-projection-timeout:" + itemNetId);
        if (car == null) throw new InvalidOperationException("train-car-projection-timeout:" + carNetId);

        Transform anchor = car.interior ?? car.transform;
        Vector3 initialLocal = anchor.InverseTransformPoint(item.transform.position);
        float maxLocalDriftObserved = 0f;
        float maxRelativeSpeedObserved = 0f;
        float maxTrainSpeedKphObserved = 0f;
        bool finite = true;
        float sampleDeadline = Time.realtimeSinceStartup + sampleSeconds;
        while (Time.realtimeSinceStartup <= sampleDeadline)
        {
            if (item == null || car == null) break;
            Vector3 local = anchor.InverseTransformPoint(item.transform.position);
            maxLocalDriftObserved = Mathf.Max(maxLocalDriftObserved,
                Vector3.Distance(initialLocal, local));
            Rigidbody itemBody = item.Item?.ItemRigidbody;
            Vector3 relativeVelocity = itemBody == null ? Vector3.zero :
                anchor.InverseTransformDirection(itemBody.velocity);
            maxRelativeSpeedObserved = Mathf.Max(maxRelativeSpeedObserved,
                relativeVelocity.magnitude);
            maxTrainSpeedKphObserved = Mathf.Max(maxTrainSpeedKphObserved,
                (car.rb?.velocity.magnitude ?? 0f) * 3.6f);
            finite &= Finite(local) && Finite(relativeVelocity) &&
                Finite(item.transform.position) && Finite(item.transform.rotation);
            yield return null;
        }

        bool activeSpatialLease = NetworkedItemManager.Instance?.HasActiveSpatialState(itemNetId) == true;
        bool hasCommitted = NetworkedItemManager.Instance != null &&
            NetworkedItemManager.Instance.TryGetCommittedSpatialState(itemNetId, out committed);
        item.TryGetPhysicalTrainParent(out TrainCar physicalParent);
        ushort physicalParentNetId = physicalParent?.GetNetId() ?? 0;
        Vector3 finalLocal = anchor.InverseTransformPoint(item.transform.position);
        bool committedParentMatches = hasCommitted &&
            committed.WorldParentKind == ItemWorldParentKind.TrainInterior &&
            committed.WorldParentNetId == carNetId;
        bool committedPoseMatches = committedParentMatches &&
            Vector3.Distance(committed.ParentLocalPosition, finalLocal) <= maximumLocalDrift;
        int representationCount = NetworkedItem.GetAll().Count(candidate =>
            candidate != null && candidate.NetId == itemNetId);

        lock (run)
        {
            run.Result["itemNetId"] = itemNetId;
            run.Result["carNetId"] = carNetId;
            run.Result["itemState"] = item.DebugCurrentState.ToString();
            run.Result["trainMoving"] = maxTrainSpeedKphObserved >= minimumTrainSpeedKph;
            run.Result["maximumTrainSpeedKphObserved"] = maxTrainSpeedKphObserved;
            run.Result["physicallyParentedToExpectedCar"] = physicalParentNetId == carNetId;
            run.Result["physicalParentNetId"] = physicalParentNetId;
            run.Result["activeSpatialLease"] = activeSpatialLease;
            run.Result["hasCommittedSpatialState"] = hasCommitted;
            run.Result["spatialSettled"] = hasCommitted && !activeSpatialLease &&
                committed.Phase == ItemSpatialPhase.Settled;
            run.Result["committedParentMatches"] = committedParentMatches;
            run.Result["committedPoseMatches"] = committedPoseMatches;
            run.Result["localPoseStable"] = maxLocalDriftObserved <= maximumLocalDrift;
            run.Result["relativeVelocityBounded"] =
                maxRelativeSpeedObserved <= maximumRelativeSpeed;
            run.Result["singleProjection"] = representationCount == 1;
            run.Result["finite"] = finite;
            run.Result["representationCount"] = representationCount;
            run.Result["maximumLocalDriftObserved"] = maxLocalDriftObserved;
            run.Result["maximumRelativeSpeedObserved"] = maxRelativeSpeedObserved;
            run.Result["initialLocalPosition"] = DebugValueSnapshotter.Snapshot(initialLocal);
            run.Result["finalLocalPosition"] = DebugValueSnapshotter.Snapshot(finalLocal);
            run.Result["positionAbsolute"] = DebugValueSnapshotter.Snapshot(
                item.transform.position - WorldMover.currentMove);
            run.Result["committedWorldParentKind"] = hasCommitted
                ? committed.WorldParentKind.ToString() : string.Empty;
            run.Result["committedWorldParentNetId"] = hasCommitted
                ? committed.WorldParentNetId : (ushort)0;
            run.Result["committedParentLocalPosition"] = hasCommitted
                ? DebugValueSnapshotter.Snapshot(committed.ParentLocalPosition) : null;
        }
    }

    private static bool Finite(Vector3 value) =>
        Finite(value.x) && Finite(value.y) && Finite(value.z);

    private static bool Finite(Quaternion value) =>
        Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static string Optional(RuntimeTestCommandDto command, string key, string fallback) =>
        command.Parameters != null && command.Parameters.TryGetValue(key, out string value)
            ? value : fallback;

    private static ushort RequiredUShort(RuntimeTestCommandDto command, string key) =>
        ushort.TryParse(Optional(command, key, string.Empty), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out ushort value) && value != 0 ? value :
            throw new ArgumentException("missing-or-invalid-parameter:" + key);

    private static float OptionalFloat(RuntimeTestCommandDto command, string key,
        float fallback) => float.TryParse(Optional(command, key, string.Empty),
        NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;

    private static bool OptionalBool(RuntimeTestCommandDto command, string key,
        bool fallback) => bool.TryParse(Optional(command, key, string.Empty), out bool value)
        ? value : fallback;
}
#endif
