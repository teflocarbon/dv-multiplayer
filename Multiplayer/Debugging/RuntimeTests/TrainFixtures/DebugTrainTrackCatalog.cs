#if DEBUG
using DV.Logic.Job;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests.TrainFixtures;

internal sealed class DebugTrainTrackRecord
{
    public RailTrack Track;
    public Track LogicTrack;
    public string GameObjectId;
    public string FullId;
    public string DisplayId;
    public bool Generic;
    public ushort NetId;
    public string Path;
    public string GeometryHash;
    public double Span;
    public int PointCount;
}

internal static class DebugTrainTrackCatalog
{
    public static IReadOnlyList<DebugTrainTrackRecord> Build()
    {
        RailTrackRegistry registry = RailTrackRegistry.Instance as RailTrackRegistry;
        if (registry == null) throw new InvalidOperationException("rail-track-registry-unavailable");
        _ = registry.AllTracks;
        List<DebugTrainTrackRecord> result = new();
        foreach (RailTrack track in RailTrackRegistryBase.RailTracks.Where(track => track != null))
        {
            RailTrackRegistry.RailTrackToLogicTrack.TryGetValue(track, out Track logic);
            TrackID id = logic?.ID;
            NetworkedRailTrack.TryGetNetId(track, out ushort netId);
            var pointSet = track.GetKinkedPointSet();
            result.Add(new DebugTrainTrackRecord
            {
                Track = track,
                LogicTrack = logic,
                GameObjectId = id?.RailTrackGameObjectID ?? track.name,
                FullId = id?.FullID ?? string.Empty,
                DisplayId = id?.FullDisplayID ?? track.name,
                Generic = id?.IsGeneric() != false,
                NetId = netId,
                Path = Path(track.transform),
                GeometryHash = GeometryHash(track),
                Span = pointSet?.span ?? 0d,
                PointCount = pointSet?.points?.Length ?? 0
            });
        }
        return result.OrderBy(record => record.GameObjectId, StringComparer.Ordinal).ToArray();
    }

    public static DebugTrainTrackRecord Resolve(string key, bool allowGeneric = false,
        string expectedGeometryHash = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("missing-or-invalid-parameter:trackId");
        DebugTrainTrackRecord[] matches = Build().Where(record =>
            string.Equals(record.GameObjectId, key, StringComparison.Ordinal) ||
            string.Equals(record.FullId, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(record.DisplayId, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(record.Path, key, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) throw new InvalidOperationException("track-not-found:" + key);
        if (matches.Length != 1) throw new InvalidOperationException("track-ambiguous:" + key);
        DebugTrainTrackRecord match = matches[0];
        if (match.NetId == 0) throw new InvalidOperationException("track-network-id-unavailable:" + key);
        if (match.Generic && !allowGeneric) throw new InvalidOperationException("generic-track-requires-explicit-opt-in:" + key);
        if (match.Generic && !string.IsNullOrWhiteSpace(expectedGeometryHash) &&
            !string.Equals(match.GeometryHash, expectedGeometryHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("generic-track-geometry-mismatch:" + key);
        return match;
    }

    public static DebugTrainTrackRecord Nearest(Vector3 localWorldPosition, float minimumEndClearance,
        out int pointIndex, out float distance)
    {
        var closest = RailTrack.GetClosest(localWorldPosition, minimumEndClearance,
            RailTrackRegistryBase.RailTracks);
        if (closest.Item1 == null || closest.Item2 == null)
            throw new InvalidOperationException("nearest-track-unavailable");
        RailTrack track = closest.Item1;
        var points = track.GetKinkedPointSet()?.points ??
            throw new InvalidOperationException("nearest-track-point-set-unavailable");
        Vector3 absolute = localWorldPosition - WorldMover.currentMove;
        pointIndex = 0;
        float best = float.PositiveInfinity;
        for (int index = 0; index < points.Length; index++)
        {
            float candidate = ((Vector3)points[index].position - absolute).sqrMagnitude;
            if (candidate >= best) continue;
            best = candidate;
            pointIndex = index;
        }
        distance = Mathf.Sqrt(best);
        return Build().Single(record => record.Track == track);
    }

    public static Dictionary<string, object> Summary(IReadOnlyList<DebugTrainTrackRecord> records)
    {
        RailTrackRegistryBase registry = RailTrackRegistryBase.Instance;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["trackCount"] = records.Count,
            ["junctionCount"] = RailTrackRegistryBase.Junctions?.Length ?? 0,
            ["tracksHash"] = registry?.TracksHash ?? string.Empty,
            ["junctionsHash"] = registry?.JunctionsHash ?? string.Empty,
            ["networkMapHash"] = NetworkMapHash(records),
            ["namedTrackCount"] = records.Count(record => !record.Generic),
            ["genericTrackCount"] = records.Count(record => record.Generic),
            ["missingNetworkIdCount"] = records.Count(record => record.NetId == 0)
        };
    }

    public static Dictionary<string, object> Snapshot(DebugTrainTrackRecord record,
        int? pointIndex = null, float? distance = null)
    {
        var points = record.Track.GetKinkedPointSet()?.points;
        Vector3? start = points?.Length > 0 ? (Vector3?)points[0].position : null;
        Vector3? end = points?.Length > 0 ? (Vector3?)points[points.Length - 1].position : null;
        Dictionary<string, object> value = new(StringComparer.Ordinal)
        {
            ["trackId"] = record.GameObjectId,
            ["fullId"] = record.FullId,
            ["displayId"] = record.DisplayId,
            ["generic"] = record.Generic,
            ["netId"] = record.NetId,
            ["path"] = record.Path,
            ["geometryHash"] = record.GeometryHash,
            ["span"] = record.Span,
            ["pointCount"] = record.PointCount,
            ["startAbsolute"] = start.HasValue ? DebugValueSnapshotter.Snapshot(start.Value) : null,
            ["endAbsolute"] = end.HasValue ? DebugValueSnapshotter.Snapshot(end.Value) : null,
            ["inConnected"] = record.Track.inIsConnected,
            ["outConnected"] = record.Track.outIsConnected,
            ["inJunction"] = record.Track.inJunction?.name ?? string.Empty,
            ["outJunction"] = record.Track.outJunction?.name ?? string.Empty
        };
        if (pointIndex.HasValue)
        {
            int index = pointIndex.Value;
            value["pointIndex"] = index;
            if (points != null && index >= 0 && index < points.Length)
            {
                value["pointAbsolute"] = DebugValueSnapshotter.Snapshot((Vector3)points[index].position);
                value["pointForward"] = DebugValueSnapshotter.Snapshot(points[index].forward);
                value["spanAtPoint"] = points[index].span;
            }
        }
        if (distance.HasValue) value["distance"] = distance.Value;
        return value;
    }

    private static string NetworkMapHash(IEnumerable<DebugTrainTrackRecord> records) => Hash(string.Join("|",
        records.OrderBy(record => record.GameObjectId, StringComparer.Ordinal)
            .Select(record => record.GameObjectId + ":" + record.NetId.ToString(CultureInfo.InvariantCulture))));

    private static string GeometryHash(RailTrack track)
    {
        var points = track.GetKinkedPointSet()?.points;
        if (points == null || points.Length == 0) return string.Empty;
        StringBuilder value = new();
        value.Append(track.name).Append('|').Append(points.Length).Append('|');
        int step = Math.Max(1, points.Length / 32);
        for (int index = 0; index < points.Length; index += step)
        {
            Vector3 point = (Vector3)points[index].position;
            value.Append(point.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.z.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }
        return Hash(value.ToString());
    }

    private static string Hash(string value)
    {
        using SHA256 algorithm = SHA256.Create();
        return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
            .Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string Path(Transform transform)
    {
        List<string> parts = new();
        while (transform != null) { parts.Add(transform.name); transform = transform.parent; }
        parts.Reverse();
        return string.Join("/", parts);
    }
}

internal sealed partial class DebugTrainTrackRuntimeDriver
{
    public IEnumerator Catalog(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        yield return null;
        IReadOnlyList<DebugTrainTrackRecord> records = DebugTrainTrackCatalog.Build();
        int limit = Mathf.Clamp(OptionalInt(command, "limit", 250), 1, 5000);
        string contains = Optional(command, "contains", string.Empty);
        IEnumerable<DebugTrainTrackRecord> selected = records;
        if (!string.IsNullOrWhiteSpace(contains)) selected = selected.Where(record =>
            record.GameObjectId.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0 ||
            record.DisplayId.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0 ||
            record.Path.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0);
        lock (run)
        {
            foreach (var field in DebugTrainTrackCatalog.Summary(records)) run.Result[field.Key] = field.Value;
            run.Result["tracks"] = selected.Take(limit).Select(record => DebugTrainTrackCatalog.Snapshot(record)).ToArray();
        }
    }

    public IEnumerator Nearest(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        yield return null;
        if (PlayerManager.PlayerTransform == null) throw new InvalidOperationException("player-transform-unavailable");
        DebugTrainTrackRecord record = DebugTrainTrackCatalog.Nearest(PlayerManager.PlayerTransform.position,
            OptionalFloat(command, "minimumEndClearance", 20f), out int index, out float distance);
        lock (run)
        {
            run.Result["track"] = DebugTrainTrackCatalog.Snapshot(record, index, distance);
            run.Result["fixtureParameters"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["trackId"] = record.GameObjectId,
                ["pointIndex"] = index.ToString(CultureInfo.InvariantCulture),
                ["withTrackDirection"] = "true",
                ["allowGenericTrack"] = record.Generic ? "true" : "false",
                ["geometryHash"] = record.GeometryHash
            };
        }
    }

    private static string Optional(RuntimeTestCommandDto command, string key, string fallback) =>
        command.Parameters != null && command.Parameters.TryGetValue(key, out string value) ? value : fallback;
    private static int OptionalInt(RuntimeTestCommandDto command, string key, int fallback) =>
        int.TryParse(Optional(command, key, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    private static float OptionalFloat(RuntimeTestCommandDto command, string key, float fallback) =>
        float.TryParse(Optional(command, key, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
}
#endif
