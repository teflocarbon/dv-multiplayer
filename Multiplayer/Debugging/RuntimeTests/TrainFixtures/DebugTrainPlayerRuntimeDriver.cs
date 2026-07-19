#if DEBUG
using DV;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests.TrainFixtures;

internal sealed class DebugTrainPlayerRuntimeDriver
{
    public IEnumerator Place(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        ushort netId = RequiredUShort(command, "carNetId");
        string anchor = Optional(command, "anchor", "interior-center").Trim().ToLowerInvariant();
        TrainCar car = null;
        DebugTrainCabAnchor cabAnchor = null;
        string anchorError = string.Empty;
        float deadline = Time.realtimeSinceStartup + 10f;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (NetworkedTrainCar.TryGet(netId, out car) && car != null &&
                (anchor != "cab" || DebugTrainCabAnchorResolver.TryResolve(car,
                    out cabAnchor, out anchorError)))
                break;
            yield return null;
        }
        if (car == null)
            throw new InvalidOperationException("fixture-car-projection-unavailable:" + netId);
        if (anchor == "cab" && cabAnchor == null)
            throw new InvalidOperationException("fixture-cab-anchor-unavailable:" + netId + ":" +
                anchorError);

        Transform parent = anchor == "cab" ? cabAnchor.Parent :
            car.interior != null ? car.interior : car.transform;
        Bounds bounds = CarSpawner.GetBoundsOfCar(car.gameObject);
        Vector3 local = anchor switch
        {
            "front-platform" => new Vector3(0f, 1.2f, bounds.extents.z * 0.55f),
            "rear-platform" => new Vector3(0f, 1.2f, -bounds.extents.z * 0.55f),
            "cab" => cabAnchor.LocalPosition,
            "interior-center" => new Vector3(0f, 1.2f, 0f),
            _ => throw new ArgumentException("unknown-train-player-anchor:" + anchor)
        };
        Vector3 destination = parent.TransformPoint(local);
        Quaternion rotation = anchor == "cab"
            ? parent.rotation * cabAnchor.LocalRotation : car.transform.rotation;
        Transform teleportTarget = anchor == "cab" ? cabAnchor.TeleportTarget : parent;
        PlayerManager.TeleportPlayer(destination, rotation, teleportTarget, true, false);
        yield return null;
        yield return new WaitForEndOfFrame();
        if (PlayerManager.Car != car)
            throw new InvalidOperationException("player-train-parenting-timeout:" + netId);
        if (anchor == "cab" && !DebugTrainCabAnchorResolver.ContainsPlayer(cabAnchor,
                PlayerManager.PlayerTransform.position))
            throw new InvalidOperationException("player-outside-resolved-cab:" + netId);
        lock (run)
        {
            run.Result["carNetId"] = netId;
            run.Result["anchor"] = anchor;
            run.Result["anchorSource"] = cabAnchor?.Source ?? "legacy-offset";
            run.Result["anchorLiveryId"] = cabAnchor?.LiveryId ?? car.carLivery?.id ?? string.Empty;
            run.Result["anchorLocalPosition"] = DebugValueSnapshotter.Snapshot(local);
            run.Result["playerInteriorLocalPosition"] = DebugValueSnapshotter.Snapshot(
                parent.InverseTransformPoint(PlayerManager.PlayerTransform.position));
            run.Result["insideResolvedCab"] = anchor != "cab" ||
                DebugTrainCabAnchorResolver.ContainsPlayer(cabAnchor,
                    PlayerManager.PlayerTransform.position);
            run.Result["teleportTarget"] = teleportTarget?.name ?? string.Empty;
            run.Result["reparentTargetPresent"] = teleportTarget != null &&
                teleportTarget.GetComponent<CharacterReparentTarget>() != null;
            run.Result["playerCar"] = PlayerManager.Car?.ID ?? string.Empty;
            run.Result["positionAbsolute"] = DebugValueSnapshotter.Snapshot(
                PlayerManager.PlayerTransform.position - WorldMover.currentMove);
        }
    }

    public IEnumerator Evacuate(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        ushort netId = RequiredUShort(command, "carNetId");
        if (!NetworkedTrainCar.TryGet(netId, out TrainCar car) || car == null)
            throw new InvalidOperationException("fixture-car-projection-unavailable:" + netId);
        float distance = Mathf.Clamp(OptionalFloat(command, "distance", 12f), 5f, 512f);
        Vector3 destination = car.transform.position + car.transform.right * distance + Vector3.up;
        PlayerManager.TeleportPlayer(destination, car.transform.rotation, null, true, false);
        yield return null;
        yield return new WaitForEndOfFrame();
        if (PlayerManager.Car == car)
            throw new InvalidOperationException("player-train-evacuation-timeout:" + netId);
        lock (run)
        {
            run.Result["carNetId"] = netId;
            run.Result["playerCar"] = PlayerManager.Car?.ID ?? string.Empty;
            run.Result["positionAbsolute"] = DebugValueSnapshotter.Snapshot(
                PlayerManager.PlayerTransform.position - WorldMover.currentMove);
        }
    }

    private static string Optional(RuntimeTestCommandDto command, string key, string fallback) =>
        command.Parameters != null && command.Parameters.TryGetValue(key, out string value) ? value : fallback;
    private static ushort RequiredUShort(RuntimeTestCommandDto command, string key) =>
        ushort.TryParse(Optional(command, key, string.Empty), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out ushort value) && value != 0 ? value :
            throw new ArgumentException("missing-or-invalid-parameter:" + key);
    private static float OptionalFloat(RuntimeTestCommandDto command, string key, float fallback) =>
        float.TryParse(Optional(command, key, string.Empty), NumberStyles.Float,
            CultureInfo.InvariantCulture, out float value) ? value : fallback;
}
#endif
