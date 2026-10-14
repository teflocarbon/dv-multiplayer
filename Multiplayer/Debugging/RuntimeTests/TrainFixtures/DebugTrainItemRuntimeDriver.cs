#if DEBUG
using DV;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Components.Networking.World.WorldItems;
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
    private readonly List<ushort> labIndex = new();

    public IEnumerator LabIndexStatus(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        PruneLabIndex();
        yield return null;
        WriteLabIndex(run);
    }

    public IEnumerator LabIndexAddLook(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        Camera camera = PlayerManager.ActiveCamera;
        if (camera == null) throw new RuntimeTestUnsupportedException("active-camera-unavailable");
        float distance = Mathf.Clamp(OptionalFloat(command, "distance", 12f), 0.5f, 50f);
        RaycastHit[] hits = Physics.RaycastAll(camera.transform.position,
            camera.transform.forward, distance, ~0, QueryTriggerInteraction.Ignore);
        NetworkedItem item = hits.OrderBy(hit => hit.distance)
            .Select(hit => hit.collider?.GetComponentInParent<NetworkedItem>())
            .FirstOrDefault(candidate => candidate != null && candidate.NetId != 0);
        if (item == null) throw new InvalidOperationException("no-networked-item-under-reticle");
        AddToLabIndex(item.NetId);
        yield return null;
        lock (run)
        {
            run.Result["addedItemNetId"] = item.NetId;
            run.Result["cameraAbsolutePosition"] = DebugValueSnapshotter.Snapshot(
                camera.transform.position - WorldMover.currentMove);
            run.Result["cameraForward"] = DebugValueSnapshotter.Snapshot(camera.transform.forward);
        }
        WriteLabIndex(run);
    }

    public IEnumerator LabIndexAddNearby(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        if (PlayerManager.PlayerTransform == null)
            throw new RuntimeTestUnsupportedException("player-transform-unavailable");
        float radius = Mathf.Clamp(OptionalFloat(command, "radius", 4f), 0.25f, 25f);
        ushort carNetId = OptionalUShort(command, "carNetId");
        int before = labIndex.Count;
        foreach (NetworkedItem item in NetworkedItem.GetAll()
            .Where(item => item != null && item.NetId != 0)
            .OrderBy(item => (item.transform.position - PlayerManager.PlayerTransform.position)
                .sqrMagnitude))
        {
            if ((item.transform.position - PlayerManager.PlayerTransform.position).sqrMagnitude >
                radius * radius)
                continue;
            if (NetworkedItemManager.Instance?.TryGetCommittedSpatialState(item.NetId,
                    out ItemSpatialStateData state) != true ||
                state.Phase != ItemSpatialPhase.Settled ||
                state.WorldParentKind != ItemWorldParentKind.TrainInterior ||
                carNetId != 0 && state.WorldParentNetId != carNetId)
                continue;
            carNetId = carNetId == 0 ? state.WorldParentNetId : carNetId;
            AddToLabIndex(item.NetId);
        }
        yield return null;
        lock (run)
        {
            run.Result["addedCount"] = labIndex.Count - before;
            run.Result["radius"] = radius;
            run.Result["carNetId"] = carNetId;
        }
        WriteLabIndex(run);
    }

    public IEnumerator LabIndexRemove(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        ushort itemNetId = RequiredUShort(command, "itemNetId");
        bool removed = labIndex.Remove(itemNetId);
        yield return null;
        lock (run)
        {
            run.Result["itemNetId"] = itemNetId;
            run.Result["removed"] = removed;
        }
        WriteLabIndex(run);
    }

    public IEnumerator LabIndexClear(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        int removed = labIndex.Count;
        labIndex.Clear();
        yield return null;
        lock (run) run.Result["removedCount"] = removed;
        WriteLabIndex(run);
    }

    public IEnumerator LabThrowFromView(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        if (NetworkLifecycle.Instance?.IsHost() != true)
            throw new InvalidOperationException("host-required");
        PruneLabIndex();
        ushort itemNetId = OptionalUShort(command, "itemNetId");
        if (itemNetId == 0 && labIndex.Count > 0) itemNetId = labIndex[labIndex.Count - 1];
        if (itemNetId == 0 || !labIndex.Contains(itemNetId))
            throw new InvalidOperationException("projectile-must-be-in-lab-index");
        if (NetworkedItemManager.Instance?.TryGetCommittedSpatialState(itemNetId,
                out ItemSpatialStateData state) != true ||
            state.Phase != ItemSpatialPhase.Settled ||
            state.WorldParentKind != ItemWorldParentKind.TrainInterior ||
            !NetworkedTrainCar.TryGet(state.WorldParentNetId, out TrainCar car) || car == null)
            throw new InvalidOperationException("projectile-must-be-settled-in-train");
        Camera camera = PlayerManager.ActiveCamera;
        if (camera == null) throw new RuntimeTestUnsupportedException("active-camera-unavailable");
        Transform anchor = car.interior ?? car.transform;
        float distance = Mathf.Clamp(OptionalFloat(command, "startDistance", 1.25f),
            0.25f, 4f);
        float speed = Mathf.Clamp(OptionalFloat(command, "speed", 8f), 0.5f, 25f);
        float lift = Mathf.Clamp(OptionalFloat(command, "lift", 0f), -3f, 3f);
        Vector3 viewDirection = camera.transform.forward.normalized;
        Vector3 worldStart = camera.transform.position + viewDirection * distance;
        Vector3 localStart = anchor.InverseTransformPoint(worldStart);
        Vector3 localDirection = anchor.InverseTransformDirection(viewDirection).normalized;
        Quaternion localRotation = Quaternion.Inverse(anchor.rotation) *
            Quaternion.LookRotation(viewDirection, camera.transform.up);
        if (!NetworkedItemManager.Instance.DebugArrangeSettledTrainItem(itemNetId,
                state.WorldParentNetId, localStart, localRotation, out string arrangeRejection))
            throw new InvalidOperationException("view-launch-arrange-rejected:" + arrangeRejection);
        Vector3 localVelocity = localDirection * speed + Vector3.up * lift;
        if (!NetworkedItemManager.Instance.ForceTrainItemWake(itemNetId,
                TrainItemWakeReason.DebugRequest, localVelocity, out string wakeRejection))
            throw new InvalidOperationException("view-launch-wake-rejected:" + wakeRejection);
        yield return null;
        lock (run)
        {
            run.Result["itemNetId"] = itemNetId;
            run.Result["carNetId"] = state.WorldParentNetId;
            run.Result["cameraAbsolutePosition"] = DebugValueSnapshotter.Snapshot(
                camera.transform.position - WorldMover.currentMove);
            run.Result["cameraForward"] = DebugValueSnapshotter.Snapshot(viewDirection);
            run.Result["launchLocalPosition"] = DebugValueSnapshotter.Snapshot(localStart);
            run.Result["initialLocalVelocity"] = DebugValueSnapshotter.Snapshot(localVelocity);
            run.Result["accepted"] = true;
        }
    }

    public IEnumerator LabStatus(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        ushort itemNetId = OptionalUShort(command, "itemNetId");
        ushort carNetId = OptionalUShort(command, "carNetId");
        yield return null;
        Dictionary<string, object> snapshot = NetworkedItemManager.Instance?
            .TrainItemWakeSnapshot(itemNetId, carNetId) ?? new();
        lock (run)
            foreach (KeyValuePair<string, object> pair in snapshot)
                run.Result[pair.Key] = pair.Value;
    }

    public IEnumerator LabVerify(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        ushort carNetId = RequiredUShort(command, "carNetId");
        ushort[] itemNetIds = RequiredUShorts(command, "itemNetIds");
        Vector3[] expectedPositions = OptionalPositions(command, "expectedLocalPositions");
        if (expectedPositions.Length != 0 && expectedPositions.Length != itemNetIds.Length)
            throw new ArgumentException("expected-position-count-mismatch");
        float maximumLayoutDrift = Mathf.Clamp(OptionalFloat(command,
            "maximumLayoutDrift", 0.2f), 0.01f, 5f);
        float deadline = Time.realtimeSinceStartup + Mathf.Clamp(OptionalFloat(command,
            "waitSeconds", 20f), 1f, 25f);
        while (Time.realtimeSinceStartup <= deadline)
        {
            bool ready = itemNetIds.All(itemNetId =>
                NetworkedItem.TryGet(itemNetId, out NetworkedItem item) && item != null &&
                NetworkedItemManager.Instance?.TryGetCommittedSpatialState(itemNetId,
                    out ItemSpatialStateData state) == true && state != null);
            if (ready) break;
            yield return null;
        }

        List<object> items = new();
        int settledCount = 0;
        int physicallyParentedCount = 0;
        int singleProjectionCount = 0;
        int layoutStableCount = 0;
        int finiteCount = 0;
        int activeCount = 0;
        float maximumObservedLayoutDrift = 0f;
        for (int index = 0; index < itemNetIds.Length; index++)
        {
            ushort itemNetId = itemNetIds[index];
            bool projected = NetworkedItem.TryGet(itemNetId, out NetworkedItem item) && item != null;
            ItemSpatialStateData state = null;
            bool committed = NetworkedItemManager.Instance?.TryGetCommittedSpatialState(itemNetId,
                out state) == true && state != null;
            bool active = NetworkedItemManager.Instance?.HasActiveSpatialState(itemNetId) == true;
            bool settled = committed && !active && state.Phase == ItemSpatialPhase.Settled &&
                state.WorldParentKind == ItemWorldParentKind.TrainInterior &&
                state.WorldParentNetId == carNetId;
            ushort physicalParentNetId = 0;
            if (projected && item.TryGetPhysicalTrainParent(out TrainCar physicalParent) &&
                physicalParent != null)
                physicalParentNetId = physicalParent.GetNetId();
            int representationCount = NetworkedItem.GetAll().Count(candidate =>
                candidate != null && candidate.NetId == itemNetId);
            float layoutDrift = expectedPositions.Length == 0 || !committed ? 0f :
                Vector3.Distance(expectedPositions[index], state.ParentLocalPosition);
            bool layoutStable = expectedPositions.Length == 0 || committed &&
                layoutDrift <= maximumLayoutDrift;
            bool finite = projected && committed && Finite(item.transform.position) &&
                Finite(item.transform.rotation) && Finite(state.AbsolutePosition) &&
                Finite(state.Rotation) && Finite(state.ParentLocalPosition) &&
                Finite(state.ParentLocalRotation);
            if (settled) settledCount++;
            if (physicalParentNetId == carNetId) physicallyParentedCount++;
            if (representationCount == 1) singleProjectionCount++;
            if (layoutStable) layoutStableCount++;
            if (finite) finiteCount++;
            if (active) activeCount++;
            maximumObservedLayoutDrift = Mathf.Max(maximumObservedLayoutDrift, layoutDrift);
            items.Add(new Dictionary<string, object>
            {
                ["itemNetId"] = itemNetId,
                ["projected"] = projected,
                ["settled"] = settled,
                ["activeSpatialLease"] = active,
                ["physicalParentNetId"] = physicalParentNetId,
                ["representationCount"] = representationCount,
                ["layoutDrift"] = layoutDrift,
                ["layoutStable"] = layoutStable,
                ["finite"] = finite
            });
        }
        bool allValid = settledCount == itemNetIds.Length &&
            physicallyParentedCount == itemNetIds.Length &&
            singleProjectionCount == itemNetIds.Length &&
            layoutStableCount == itemNetIds.Length && finiteCount == itemNetIds.Length &&
            activeCount == 0;
        lock (run)
        {
            run.Result["expectedCount"] = itemNetIds.Length;
            run.Result["settledCount"] = settledCount;
            run.Result["physicallyParentedCount"] = physicallyParentedCount;
            run.Result["singleProjectionCount"] = singleProjectionCount;
            run.Result["layoutStableCount"] = layoutStableCount;
            run.Result["finiteCount"] = finiteCount;
            run.Result["activeCount"] = activeCount;
            run.Result["maximumLayoutDrift"] = maximumObservedLayoutDrift;
            run.Result["allValid"] = allValid;
            run.Result["items"] = items.ToArray();
        }
    }

    public IEnumerator LabArrange(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        if (NetworkLifecycle.Instance?.IsHost() != true)
            throw new InvalidOperationException("host-required");
        ushort carNetId = RequiredUShort(command, "carNetId");
        ushort[] itemNetIds = RequiredUShorts(command, "itemNetIds");
        string layout = Optional(command, "layout", "stack").Trim().ToLowerInvariant();
        float explicitX = 0f;
        float explicitY = 0f;
        float explicitZ = 0f;
        bool explicitPosition = TryFloat(command, "x", out explicitX) &
            TryFloat(command, "y", out explicitY) &
            TryFloat(command, "z", out explicitZ);
        ItemSpatialStateData anchorState = null;
        bool hasAnchor = NetworkedItemManager.Instance?.TryGetCommittedSpatialState(itemNetIds[0],
                out anchorState) == true &&
            anchorState.WorldParentKind == ItemWorldParentKind.TrainInterior &&
            anchorState.WorldParentNetId == carNetId;
        if (!hasAnchor && !explicitPosition)
            throw new InvalidOperationException("arrange-position-required-for-world-item");
        // A railcar's model origin is not a usable interior coordinate (for example, the
        // DE2 origin is below and outside its cab).  Unless the caller deliberately supplies
        // coordinates, arrange around the first settled item's known-good floor position.
        Vector3 anchorLocal = hasAnchor ? anchorState.ParentLocalPosition :
            new Vector3(explicitX, explicitY, explicitZ);
        float x = OptionalFloat(command, "x", anchorLocal.x);
        float y = OptionalFloat(command, "y", anchorLocal.y);
        float z = OptionalFloat(command, "z", anchorLocal.z);
        Quaternion rotation = new(OptionalFloat(command, "rotationX", 0f),
            OptionalFloat(command, "rotationY", 0f),
            OptionalFloat(command, "rotationZ", 0f),
            OptionalFloat(command, "rotationW", 1f));
        float rotationMagnitudeSquared = rotation.x * rotation.x + rotation.y * rotation.y +
            rotation.z * rotation.z + rotation.w * rotation.w;
        rotation = rotationMagnitudeSquared > 0.0001f ? Normalize(rotation) : Quaternion.identity;
        float horizontalSpacing = Mathf.Clamp(OptionalFloat(command,
            "horizontalSpacing", 0.45f), 0.05f, 3f);
        float verticalSpacing = Mathf.Clamp(OptionalFloat(command,
            "verticalSpacing", 0f), 0f, 3f);
        int columns = Mathf.Clamp(OptionalInt(command, "columns", 4), 1, 16);
        List<object> arranged = new();
        float stackY = y;
        float previousHeight = 0f;
        for (int index = 0; index < itemNetIds.Length; index++)
        {
            float currentHeight = ItemHeight(itemNetIds[index]);
            if (index > 0 && layout is "tower" or "stack")
                stackY += verticalSpacing > 0f ? verticalSpacing :
                    previousHeight * 0.5f + currentHeight * 0.5f + 0.005f;
            Vector3 local = layout switch
            {
                "grid" => new Vector3(x + index % columns * horizontalSpacing, y,
                    z + index / columns * horizontalSpacing),
                "tower" or "stack" => new Vector3(x, stackY, z),
                "line" => new Vector3(x + index * horizontalSpacing, y, z),
                _ => throw new ArgumentException("invalid-train-item-lab-layout:" + layout)
            };
            if (!NetworkedItemManager.Instance.DebugArrangeSettledTrainItem(itemNetIds[index],
                    carNetId, local, rotation, out string rejection))
                throw new InvalidOperationException($"arrange-rejected:{itemNetIds[index]}:{rejection}");
            arranged.Add(new Dictionary<string, object>
            {
                ["itemNetId"] = itemNetIds[index],
                ["localPosition"] = DebugValueSnapshotter.Snapshot(local)
            });
            previousHeight = currentHeight;
            yield return null;
        }
        lock (run)
        {
            run.Result["carNetId"] = carNetId;
            run.Result["layout"] = layout;
            run.Result["arrangedCount"] = arranged.Count;
            run.Result["arranged"] = arranged.ToArray();
        }
    }

    public IEnumerator LabWake(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        if (NetworkLifecycle.Instance?.IsHost() != true)
            throw new InvalidOperationException("host-required");
        ushort itemNetId = RequiredUShort(command, "itemNetId");
        Vector3 velocity = new(OptionalFloat(command, "velocityX", 0f),
            OptionalFloat(command, "velocityY", 0f),
            OptionalFloat(command, "velocityZ", 0f));
        bool accepted = NetworkedItemManager.Instance.ForceTrainItemWake(itemNetId,
            TrainItemWakeReason.DebugRequest, velocity, out string rejection);
        yield return null;
        lock (run)
        {
            run.Result["itemNetId"] = itemNetId;
            run.Result["accepted"] = accepted;
            run.Result["rejection"] = rejection;
            run.Result["initialLocalVelocity"] = DebugValueSnapshotter.Snapshot(velocity);
        }
        if (!accepted) throw new InvalidOperationException("wake-rejected:" + rejection);
    }

    public IEnumerator LabThrowAt(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        if (NetworkLifecycle.Instance?.IsHost() != true)
            throw new InvalidOperationException("host-required");
        ushort sourceItemNetId = RequiredUShort(command, "sourceItemNetId");
        ushort targetItemNetId = RequiredUShort(command, "targetItemNetId");
        if (NetworkedItemManager.Instance?.TryGetCommittedSpatialState(sourceItemNetId,
                out ItemSpatialStateData source) != true ||
            NetworkedItemManager.Instance.TryGetCommittedSpatialState(targetItemNetId,
                out ItemSpatialStateData target) != true ||
            source.WorldParentKind != ItemWorldParentKind.TrainInterior ||
            target.WorldParentKind != ItemWorldParentKind.TrainInterior ||
            source.WorldParentNetId != target.WorldParentNetId)
            throw new InvalidOperationException("source-and-target-must-be-settled-in-same-train");
        float speed = Mathf.Clamp(OptionalFloat(command, "speed", 6f), 0.5f, 20f);
        float lift = Mathf.Clamp(OptionalFloat(command, "lift", 0.15f), -2f, 2f);
        Vector3 direction = target.ParentLocalPosition - source.ParentLocalPosition;
        if (direction.sqrMagnitude < 0.01f)
            throw new InvalidOperationException("source-and-target-overlap");
        Vector3 velocity = direction.normalized * speed + Vector3.up * lift;
        bool accepted = NetworkedItemManager.Instance.ForceTrainItemWake(sourceItemNetId,
            TrainItemWakeReason.DebugRequest, velocity, out string rejection);
        yield return null;
        lock (run)
        {
            run.Result["sourceItemNetId"] = sourceItemNetId;
            run.Result["targetItemNetId"] = targetItemNetId;
            run.Result["carNetId"] = source.WorldParentNetId;
            run.Result["accepted"] = accepted;
            run.Result["rejection"] = rejection;
            run.Result["initialLocalVelocity"] = DebugValueSnapshotter.Snapshot(velocity);
        }
        if (!accepted) throw new InvalidOperationException("throw-at-rejected:" + rejection);
    }

    public IEnumerator LabWaitForWakeCycle(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        ushort itemNetId = RequiredUShort(command, "itemNetId");
        uint previousRevision = RequiredUInt(command, "previousAuthorityRevision");
        float waitSeconds = Mathf.Clamp(OptionalFloat(command, "waitSeconds", 20f), 1f, 60f);
        bool activeObserved = false;
        ItemSpatialStateData committed = null;
        float deadline = Time.realtimeSinceStartup + waitSeconds;
        while (Time.realtimeSinceStartup <= deadline)
        {
            activeObserved |= NetworkedItemManager.Instance?.HasActiveSpatialState(itemNetId) == true;
            bool settled = NetworkedItemManager.Instance?.TryGetCommittedSpatialState(itemNetId,
                out committed) == true && committed.Phase == ItemSpatialPhase.Settled &&
                committed.AuthorityRevision > previousRevision &&
                NetworkedItemManager.Instance.HasActiveSpatialState(itemNetId) == false;
            if (settled) break;
            yield return null;
        }
        bool completed = committed != null && committed.Phase == ItemSpatialPhase.Settled &&
            committed.AuthorityRevision > previousRevision &&
            NetworkedItemManager.Instance?.HasActiveSpatialState(itemNetId) != true;
        lock (run)
        {
            run.Result["itemNetId"] = itemNetId;
            run.Result["previousAuthorityRevision"] = previousRevision;
            run.Result["authorityRevision"] = committed?.AuthorityRevision ?? 0;
            run.Result["authorityRevisionAdvanced"] = (committed?.AuthorityRevision ?? 0) > previousRevision;
            run.Result["activeObserved"] = activeObserved;
            run.Result["wakeCycleCompleted"] = completed;
            run.Result["spatialSettled"] = completed;
            run.Result["worldParentKind"] = committed?.WorldParentKind.ToString() ?? string.Empty;
            run.Result["trainCarNetId"] = committed?.WorldParentNetId ?? 0;
        }
        if (!completed) throw new InvalidOperationException("train-item-wake-cycle-timeout");
    }

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

    private static Quaternion Normalize(Quaternion value)
    {
        float inverseMagnitude = 1f / Mathf.Sqrt(value.x * value.x + value.y * value.y +
            value.z * value.z + value.w * value.w);
        return new Quaternion(value.x * inverseMagnitude, value.y * inverseMagnitude,
            value.z * inverseMagnitude, value.w * inverseMagnitude);
    }

    private void AddToLabIndex(ushort itemNetId)
    {
        if (!labIndex.Contains(itemNetId)) labIndex.Add(itemNetId);
    }

    private void PruneLabIndex() => labIndex.RemoveAll(itemNetId =>
        !NetworkedItem.TryGet(itemNetId, out NetworkedItem item) || item == null);

    private void WriteLabIndex(RuntimeTestRunDto run)
    {
        PruneLabIndex();
        object[] items = labIndex.Select((itemNetId, index) =>
        {
            NetworkedItem.TryGet(itemNetId, out NetworkedItem item);
            ItemSpatialStateData state = null;
            bool committed = NetworkedItemManager.Instance != null &&
                NetworkedItemManager.Instance.TryGetCommittedSpatialState(itemNetId, out state);
            return (object)new Dictionary<string, object>
            {
                ["index"] = index,
                ["itemNetId"] = itemNetId,
                ["name"] = item?.Item?.name ?? item?.name ?? string.Empty,
                ["itemState"] = item?.DebugCurrentState.ToString() ?? string.Empty,
                ["settled"] = committed && state.Phase == ItemSpatialPhase.Settled,
                ["carNetId"] = committed &&
                    state.WorldParentKind == ItemWorldParentKind.TrainInterior
                        ? state.WorldParentNetId : (ushort)0,
                ["authorityRevision"] = committed ? state.AuthorityRevision : 0u
            };
        }).ToArray();
        lock (run)
        {
            run.Result["labItemCount"] = items.Length;
            run.Result["labItems"] = items;
        }
    }

    private static string Optional(RuntimeTestCommandDto command, string key, string fallback) =>
        command.Parameters != null && command.Parameters.TryGetValue(key, out string value)
            ? value : fallback;

    private static ushort RequiredUShort(RuntimeTestCommandDto command, string key) =>
        ushort.TryParse(Optional(command, key, string.Empty), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out ushort value) && value != 0 ? value :
            throw new ArgumentException("missing-or-invalid-parameter:" + key);

    private static ushort OptionalUShort(RuntimeTestCommandDto command, string key) =>
        ushort.TryParse(Optional(command, key, string.Empty), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out ushort value) ? value : (ushort)0;

    private static ushort[] RequiredUShorts(RuntimeTestCommandDto command, string key)
    {
        ushort[] values = Optional(command, key, string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => ushort.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out ushort parsed) ? parsed : (ushort)0)
            .Where(value => value != 0).Distinct().ToArray();
        if (values.Length == 0)
            throw new ArgumentException("missing-or-invalid-parameter:" + key);
        return values;
    }

    private static Vector3[] OptionalPositions(RuntimeTestCommandDto command, string key)
    {
        string raw = Optional(command, key, string.Empty);
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<Vector3>();
        return raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(position => position.Split(new[] { ',' },
                StringSplitOptions.RemoveEmptyEntries))
            .Select(parts => parts.Length == 3 &&
                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float x) &&
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float y) &&
                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float z) ? new Vector3(x, y, z) :
                    throw new ArgumentException("invalid-position-list:" + key))
            .ToArray();
    }

    private static uint RequiredUInt(RuntimeTestCommandDto command, string key) =>
        uint.TryParse(Optional(command, key, string.Empty), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out uint value) ? value :
            throw new ArgumentException("missing-or-invalid-parameter:" + key);

    private static float ItemHeight(ushort itemNetId)
    {
        if (!NetworkedItem.TryGet(itemNetId, out NetworkedItem item) || item == null)
            return 0.25f;
        Collider[] colliders = item.GetComponentsInChildren<Collider>(true)
            .Where(collider => collider != null && !collider.isTrigger).ToArray();
        return colliders.Length == 0 ? 0.25f :
            Mathf.Clamp(colliders.Max(collider => collider.bounds.size.y), 0.05f, 2f);
    }

    private static int OptionalInt(RuntimeTestCommandDto command, string key, int fallback) =>
        int.TryParse(Optional(command, key, string.Empty), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private static float OptionalFloat(RuntimeTestCommandDto command, string key,
        float fallback) => float.TryParse(Optional(command, key, string.Empty),
        NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;

    private static bool TryFloat(RuntimeTestCommandDto command, string key, out float value) =>
        float.TryParse(Optional(command, key, string.Empty), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value);

    private static bool OptionalBool(RuntimeTestCommandDto command, string key,
        bool fallback) => bool.TryParse(Optional(command, key, string.Empty), out bool value)
        ? value : fallback;
}
#endif
