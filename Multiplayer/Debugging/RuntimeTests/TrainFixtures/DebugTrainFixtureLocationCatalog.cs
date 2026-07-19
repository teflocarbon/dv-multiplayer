#if DEBUG
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests.TrainFixtures;

internal sealed class DebugTrainFixtureLocationRecord
{
    public string Id;
    public string DisplayName;
    public string TrackId;
    public int PointIndex;
    public bool WithTrackDirection;
    public bool AllowGenericTrack;
    public string GeometryHash;
    public Vector3 PlayerAbsolutePosition;
    public float PlayerYaw;
}

internal static class DebugTrainFixtureLocationCatalog
{
    private static readonly DebugTrainFixtureLocationRecord[] locations =
    {
        new()
        {
            Id = "steel-mill-north-test-track",
            DisplayName = "Steel Mill north test track",
            TrackId = "[Y]_[#Y]_[#S-550-#T]",
            PointIndex = 457,
            WithTrackDirection = true,
            AllowGenericTrack = true,
            GeometryHash = "15844e45f342676bf9fac0d360d8eae61cc99099f3deae2e1ad40e3198bc47b5",
            PlayerAbsolutePosition = new Vector3(8217.779f, 129.884064f, 7729.95654f),
            PlayerYaw = 128.5f
        }
    };

    public static IReadOnlyList<DebugTrainFixtureLocationRecord> All => locations;

    public static DebugTrainFixtureLocationRecord Resolve(string id)
    {
        DebugTrainFixtureLocationRecord[] matches = locations.Where(location =>
            string.Equals(location.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) throw new InvalidOperationException("train-fixture-location-not-found:" + id);
        if (matches.Length != 1) throw new InvalidOperationException("train-fixture-location-ambiguous:" + id);
        return matches[0];
    }

    public static Dictionary<string, object> Snapshot(DebugTrainFixtureLocationRecord location)
    {
        Dictionary<string, object> result = new(StringComparer.Ordinal)
        {
            ["locationId"] = location.Id,
            ["displayName"] = location.DisplayName,
            ["trackId"] = location.TrackId,
            ["pointIndex"] = location.PointIndex,
            ["withTrackDirection"] = location.WithTrackDirection,
            ["allowGenericTrack"] = location.AllowGenericTrack,
            ["geometryHash"] = location.GeometryHash,
            ["playerAbsolutePosition"] = DebugValueSnapshotter.Snapshot(location.PlayerAbsolutePosition),
            ["playerYaw"] = location.PlayerYaw
        };
        try
        {
            DebugTrainTrackRecord track = DebugTrainTrackCatalog.Resolve(location.TrackId,
                location.AllowGenericTrack, location.GeometryHash);
            result["trackLoaded"] = true;
            result["track"] = DebugTrainTrackCatalog.Snapshot(track, location.PointIndex);
        }
        catch (InvalidOperationException exception)
        {
            result["trackLoaded"] = false;
            result["trackError"] = exception.Message;
        }
        return result;
    }

    public static IEnumerator TeleportAndWaitForTrack(DebugTrainFixtureLocationRecord location,
        float timeoutSeconds = 15f)
    {
        if (PlayerManager.PlayerTransform == null)
            throw new InvalidOperationException("player-transform-unavailable");

        Vector3 local = location.PlayerAbsolutePosition + WorldMover.currentMove;
        Quaternion rotation = Quaternion.Euler(0f, location.PlayerYaw, 0f);
        PlayerManager.TeleportPlayer(local, rotation, null, true, false);
        yield return null;
        yield return new WaitForEndOfFrame();

        float deadline = Time.realtimeSinceStartup + Mathf.Max(1f, timeoutSeconds);
        string lastError = "track-not-yet-observed";
        while (Time.realtimeSinceStartup <= deadline)
        {
            try
            {
                _ = DebugTrainTrackCatalog.Resolve(location.TrackId,
                    location.AllowGenericTrack, location.GeometryHash);
                yield break;
            }
            catch (InvalidOperationException exception) when (
                exception.Message.StartsWith("track-not-found:", StringComparison.Ordinal) ||
                exception.Message.StartsWith("track-network-id-unavailable:",
                    StringComparison.Ordinal) ||
                exception.Message == "rail-track-registry-unavailable")
            {
                lastError = exception.Message;
            }
            yield return null;
        }
        throw new InvalidOperationException("fixture-location-streaming-timeout:" +
            location.Id + ":" + lastError);
    }
}

internal sealed partial class DebugTrainTrackRuntimeDriver
{
    public IEnumerator FixtureLocations(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        yield return null;
        lock (run)
            run.Result["locations"] = DebugTrainFixtureLocationCatalog.All
                .Select(DebugTrainFixtureLocationCatalog.Snapshot).ToArray();
    }

    public IEnumerator TeleportToFixtureLocation(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        DebugTrainFixtureLocationRecord location = DebugTrainFixtureLocationCatalog.Resolve(
            Required(command, "locationId"));
        IEnumerator load = DebugTrainFixtureLocationCatalog.TeleportAndWaitForTrack(location);
        while (load.MoveNext()) yield return load.Current;

        Vector3 actual = PlayerManager.PlayerTransform.position - WorldMover.currentMove;
        float error = Vector3.Distance(actual, location.PlayerAbsolutePosition);
        lock (run)
        {
            run.Result["location"] = DebugTrainFixtureLocationCatalog.Snapshot(location);
            run.Result["actualAbsolutePosition"] = DebugValueSnapshotter.Snapshot(actual);
            run.Result["positionError"] = error;
        }
        if (error > 0.75f) throw new InvalidOperationException("fixture-location-teleport-mismatch:" + error);
    }

    private static string Required(RuntimeTestCommandDto command, string key)
    {
        if (command.Parameters == null || !command.Parameters.TryGetValue(key, out string value) ||
            string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("missing-or-invalid-parameter:" + key);
        return value;
    }
}
#endif
