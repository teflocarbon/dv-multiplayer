using LiteNetLib;
using Multiplayer.Components;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Multiplayer.Debugging;

/// <summary>
/// Bounded, debug-only health telemetry. All expensive scans are performed at low frequency
/// and all hot-path entry points only update counters or small records.
/// </summary>
public static class DebugDiagnostics
{
    public sealed class QueueHealth
    {
        public string Key;
        public string Kind;
        public string EntityId;
        public int Depth;
        public int MaximumDepth;
        public uint LastReceivedTick;
        public uint LastAppliedTick;
        public long Received;
        public long Applied;
        public long StaleDiscarded;
        public long Cleared;
        public long TickGaps;
        public uint LargestTickGap;
        public DateTime OldestUtc;
        public DateTime UpdatedUtc;
    }

    public sealed class DependencyHealth
    {
        public string Key;
        public string EntityType;
        public string EntityId;
        public string Reason;
        public string Detail;
        public int Count;
        public DateTime FirstSeenUtc;
        public DateTime LastSeenUtc;
    }

    public sealed class IdentityIssue
    {
        public string Code;
        public string EntityId;
        public string Detail;
        public DebugSeverity Severity;
    }

    public sealed class NetworkHealth
    {
        public double TickIntervalAverageMs;
        public double TickJitterAverageMs;
        public double TickJitterMaximumMs;
        public double ObservabilityAverageMs;
        public double ObservabilityMaximumMs;
        public double FrameAverageMs;
        public double FrameMaximumMs;
        public long EventsPerSecond;
        public long PacketsInPerSecond;
        public long PacketsOutPerSecond;
        public long BytesInPerSecond;
        public long BytesOutPerSecond;
        public int LatencyLatestMs;
        public double LatencyAverageMs;
        public double LatencyJitterMs;
        public int LatencyMaximumMs;
        public long AutoCaptureCount;
        public string LastAutoTrigger = string.Empty;
    }

    private sealed class MutableQueue
    {
        public readonly QueueHealth Value = new();
    }

    private static readonly object gate = new();
    private static readonly Dictionary<string, Dictionary<string, MutableQueue>> queues = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DependencyHealth> dependencies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<IdentityIssue> identityIssues = new();
    private static readonly NetworkHealth network = new();
    private static DebugEventStore store;
    private static long packetInCount, packetOutCount, bytesInCount, bytesOutCount, eventCount;
    private static long previousPacketIn, previousPacketOut, previousBytesIn, previousBytesOut, previousEvents;
    private static double latencyMean, latencyDeviation;
    private static long latencySamples;
    private static double tickMean, tickDeviation;
    private static long tickSamples;
    private static long lastTickTimestamp;
    private static double observabilityTotal, observabilityMax, frameTotal, frameMax;
    private static int observabilitySamples, frameSamples;
    private static float nextSecond, nextAudit, nextSummary;
    private static string pendingAutoTrigger;
    private static float autoCaptureStopAt;
    private static float nextAutoCaptureAllowed;
    private static readonly Dictionary<string, float> autoTriggerCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private const int MaximumAutomaticCapturesPerSession = 10;
    private static FieldInfo itemIndexField;
    private static bool started;

    public static bool AutomaticCapturesEnabled { get; set; }

    public static void Start(DebugEventStore eventStore)
    {
        Stop();
        store = eventStore;
        if (store != null) store.Published += OnPublished;
        started = true;
        nextSecond = nextAudit = nextSummary = 0;
    }

    public static void Stop()
    {
        if (store != null) store.Published -= OnPublished;
        store = null;
        lock (gate) { queues.Clear(); dependencies.Clear(); identityIssues.Clear(); autoTriggerCooldowns.Clear(); }
        started = false;
        pendingAutoTrigger = null;
    }

    public static IDisposable Measure(string name) => !started ? EmptyScope.Instance : new MeasureScope(name);

    public static void RecordFrame(float milliseconds)
    {
        if (!started) return;
        frameTotal += milliseconds; frameSamples++; if (milliseconds > frameMax) frameMax = milliseconds;
    }

    public static void RecordPacket(bool inbound, int bytes)
    {
        if (!started) return;
        if (inbound) { packetInCount++; bytesInCount += Math.Max(0, bytes); }
        else { packetOutCount++; bytesOutCount += Math.Max(0, bytes); }
    }

    public static void RecordLatency(DebugRuntimeSide side, int peerId, int milliseconds)
    {
        if (!started) return;
        latencySamples++;
        double delta = milliseconds - latencyMean;
        latencyMean += delta / latencySamples;
        latencyDeviation += (Math.Abs(delta) - latencyDeviation) / latencySamples;
        network.LatencyLatestMs = milliseconds;
        network.LatencyAverageMs = latencyMean;
        network.LatencyJitterMs = latencyDeviation;
        network.LatencyMaximumMs = Math.Max(network.LatencyMaximumMs, milliseconds);
    }

    public static void RecordNetworkTick(uint tick)
    {
        if (!started) return;
        long now = Stopwatch.GetTimestamp();
        if (lastTickTimestamp != 0)
        {
            double interval = (now - lastTickTimestamp) * 1000d / Stopwatch.Frequency;
            double expected = 1000d / NetworkLifecycle.TICK_RATE;
            double jitter = Math.Abs(interval - expected);
            tickSamples++;
            tickMean += (interval - tickMean) / tickSamples;
            tickDeviation += (jitter - tickDeviation) / tickSamples;
            network.TickIntervalAverageMs = tickMean;
            network.TickJitterAverageMs = tickDeviation;
            network.TickJitterMaximumMs = Math.Max(network.TickJitterMaximumMs, jitter);
        }
        lastTickTimestamp = now;
    }

    public static void QueueReceived(string kind, string entityId, int depth, uint tick, uint previousTick)
    {
        if (!started) return;
        lock (gate)
        {
            QueueHealth value = GetQueue(kind, entityId).Value;
            value.Received++; value.Depth = depth; value.MaximumDepth = Math.Max(value.MaximumDepth, depth);
            value.LastReceivedTick = tick; value.UpdatedUtc = DateTime.UtcNow;
            if (depth == 1) value.OldestUtc = value.UpdatedUtc;
            if (previousTick > 0 && tick > previousTick + 1)
            {
                value.TickGaps++; value.LargestTickGap = Math.Max(value.LargestTickGap, tick - previousTick);
            }
        }
    }

    public static void QueueStale(string kind, string entityId, int depth, uint tick, uint lastTick)
    {
        if (!started) return;
        lock (gate)
        {
            QueueHealth value = GetQueue(kind, entityId).Value;
            value.StaleDiscarded++; value.Depth = depth; value.UpdatedUtc = DateTime.UtcNow;
        }
    }

    public static void QueueApplied(string kind, string entityId, int depth, uint tick)
    {
        if (!started) return;
        lock (gate)
        {
            QueueHealth value = GetQueue(kind, entityId).Value;
            value.Applied++; value.Depth = depth; value.LastAppliedTick = tick; value.UpdatedUtc = DateTime.UtcNow;
            if (depth == 0) value.OldestUtc = default;
        }
    }

    public static void QueueCleared(string kind, string entityId, int previousDepth)
    {
        if (!started) return;
        lock (gate)
        {
            QueueHealth value = GetQueue(kind, entityId).Value;
            value.Cleared += previousDepth; value.Depth = 0; value.OldestUtc = default; value.UpdatedUtc = DateTime.UtcNow;
        }
    }

    public static void ReportDependency(string entityType, string entityId, string reason, string detail = "")
    {
        if (!started) return;
        string key = $"{entityType}:{entityId}:{reason}";
        lock (gate)
        {
            if (!dependencies.TryGetValue(key, out DependencyHealth value))
                dependencies[key] = value = new DependencyHealth { Key = key, EntityType = entityType, EntityId = entityId, Reason = reason, FirstSeenUtc = DateTime.UtcNow };
            value.Detail = detail ?? string.Empty; value.Count++; value.LastSeenUtc = DateTime.UtcNow;
        }
    }

    public static void ResolveDependency(string entityType, string entityId, string reason)
    {
        lock (gate) dependencies.Remove($"{entityType}:{entityId}:{reason}");
    }

    public static QueueHealth[] QueueSnapshot()
    {
        lock (gate) return queues.Values.SelectMany(kind => kind.Values).Select(item => Copy(item.Value)).OrderByDescending(item => item.Depth).ThenBy(item => item.Key).ToArray();
    }

    public static DependencyHealth[] DependencySnapshot()
    {
        lock (gate) return dependencies.Values.Select(Copy).OrderBy(item => item.FirstSeenUtc).ToArray();
    }

    public static IdentityIssue[] IdentitySnapshot()
    {
        lock (gate) return identityIssues.Select(Copy).ToArray();
    }

    public static NetworkHealth HealthSnapshot()
    {
        lock (gate) return Copy(network);
    }

    public static Dictionary<string, object>[] InterestMatrix(string itemId)
    {
        if (!ushort.TryParse(itemId, out ushort id) || !NetworkedItem.TryGet(id, out NetworkedItem item) || item == null || NetworkLifecycle.Instance?.Server == null)
            return Array.Empty<Dictionary<string, object>>();
        return NetworkLifecycle.Instance.Server.ServerPlayers.Select(player =>
        {
            bool nearby = player.NearbyItems.TryGetValue(item, out float nearbyTime);
            bool known = player.KnownItems.TryGetValue(item, out uint knownTick);
            float distance = Vector3.Distance(player.WorldPosition, item.transform.position);
            string action = player.LoadingState < PlayerLoadingState.ReadyForItems ? "not-ready" : !nearby ? "outside-interest" : !known ? "create-required" : knownTick < item.LastDirtyTick ? "full-sync-required" : "up-to-date";
            return new Dictionary<string, object>
            {
                ["playerId"] = player.PlayerId, ["player"] = player.Username, ["loadingState"] = player.LoadingState.ToString(),
                ["distance"] = distance, ["nearby"] = nearby, ["nearbyAgeSeconds"] = nearby ? Time.time - nearbyTime : 0,
                ["known"] = known, ["knownTick"] = knownTick, ["lastDirtyTick"] = item.LastDirtyTick, ["decision"] = action
            };
        }).ToArray();
    }

    public static Dictionary<string, object>[] ReplicationFlow(string itemId)
    {
        DebugEntityDto entity = EntityDebugRegistry.Get("Item", itemId);
        if (entity == null) return Array.Empty<Dictionary<string, object>>();
        string[] stages = { "item.snapshot-created", "item.packet-send-requested", "packet.handler.before", "item.validation-accepted", "item.validation-rejected", "item.snapshot-received", "item.snapshot-apply.before", "item.snapshot-apply.after", "item.relay-requested", "item.missing-local-representation" };
        return stages.Select(stage =>
        {
            DebugEvent match = entity.Timeline.LastOrDefault(item => string.Equals(item.EventName, stage, StringComparison.OrdinalIgnoreCase));
            return new Dictionary<string, object>
            {
                ["stage"] = stage, ["observed"] = match != null, ["timestampUtc"] = match?.TimestampUtc,
                ["sequence"] = match?.Sequence ?? 0, ["side"] = match?.RuntimeSide.ToString() ?? string.Empty,
                ["severity"] = match?.Severity.ToString() ?? string.Empty
            };
        }).ToArray();
    }

    public static void Tick()
    {
        if (!started) return;
        float now = Time.unscaledTime;
        if (now >= nextSecond)
        {
            nextSecond = now + 1f;
            lock (gate)
            {
                network.PacketsInPerSecond = packetInCount - previousPacketIn; previousPacketIn = packetInCount;
                network.PacketsOutPerSecond = packetOutCount - previousPacketOut; previousPacketOut = packetOutCount;
                network.BytesInPerSecond = bytesInCount - previousBytesIn; previousBytesIn = bytesInCount;
                network.BytesOutPerSecond = bytesOutCount - previousBytesOut; previousBytesOut = bytesOutCount;
                network.EventsPerSecond = eventCount - previousEvents; previousEvents = eventCount;
                network.ObservabilityAverageMs = observabilitySamples == 0 ? 0 : observabilityTotal / observabilitySamples;
                network.ObservabilityMaximumMs = observabilityMax;
                network.FrameAverageMs = frameSamples == 0 ? 0 : frameTotal / frameSamples;
                network.FrameMaximumMs = frameMax;
                observabilityTotal = observabilityMax = frameTotal = frameMax = 0; observabilitySamples = frameSamples = 0;
            }
        }
        if (now >= nextAudit)
        {
            nextAudit = now + 5f;
            AuditIdentities();
            AuditBlockedQueues();
        }
        if (now >= nextSummary)
        {
            nextSummary = now + 5f;
            DebugRuntime.Publish("diagnostics", "diagnostics.health-sample", DebugRuntimeSide.Shared, data: DebugValueSnapshotter.SnapshotObject(HealthSnapshot()), highFrequency: true, samplingDecided: true);
        }
        if (AutomaticCapturesEnabled && network.AutoCaptureCount < MaximumAutomaticCapturesPerSession && pendingAutoTrigger != null && now >= nextAutoCaptureAllowed && DebugRuntime.Captures?.IsCapturing != true)
        {
            string trigger = pendingAutoTrigger; pendingAutoTrigger = null; nextAutoCaptureAllowed = now + 60f;
            network.AutoCaptureCount++; network.LastAutoTrigger = trigger;
            DebugRuntime.StartCapture("auto-" + Sanitize(trigger)); autoCaptureStopAt = now + 8f;
            DebugRuntime.Publish("diagnostics", "diagnostics.auto-capture-triggered", DebugRuntimeSide.Shared, DebugSeverity.Warning, data: new() { ["trigger"] = trigger, ["durationSeconds"] = 8 });
        }
        if (autoCaptureStopAt > 0 && now >= autoCaptureStopAt && DebugRuntime.Captures?.IsCapturing == true)
        {
            autoCaptureStopAt = 0; DebugRuntime.StopCapture();
        }
    }

    private static void OnPublished(DebugEvent item)
    {
        eventCount++;
        if (!AutomaticCapturesEnabled || item == null || item.Category is "capture" or "diagnostics") return;
        if (item.EventName.Contains("validation-rejected") || item.EventName.Contains("desync") || item.Severity >= DebugSeverity.Error)
            RequestAutoCapture($"{item.EventName}-{item.EntityType}-{item.EntityId}");
    }

    private static void AuditBlockedQueues()
    {
        QueueHealth blocked = QueueSnapshot().FirstOrDefault(item => item.Depth > 0 && item.OldestUtc != default && (DateTime.UtcNow - item.OldestUtc).TotalSeconds > 2);
        if (blocked != null) RequestAutoCapture($"queue-blocked-{blocked.Kind}-{blocked.EntityId}");
        DependencyHealth dependency = DependencySnapshot().FirstOrDefault(item => (DateTime.UtcNow - item.FirstSeenUtc).TotalSeconds > 2);
        if (dependency != null) RequestAutoCapture($"dependency-{dependency.Reason}-{dependency.EntityId}");
    }

    private static void AuditIdentities()
    {
        List<IdentityIssue> issues = new();
        NetworkedItem[] items = NetworkedItem.GetAll().Where(item => item != null).Distinct().ToArray();
        foreach (IGrouping<ushort, NetworkedItem> group in items.GroupBy(item => item.NetId).Where(group => group.Key != 0 && group.Count() > 1))
            issues.Add(new IdentityIssue { Code = "duplicate-network-id", EntityId = group.Key.ToString(), Severity = DebugSeverity.Error, Detail = string.Join(", ", group.Select(item => $"{item.name}#{item.gameObject.GetInstanceID()}")) });
        foreach (NetworkedItem item in items)
        {
            if (!NetworkLifecycle.Instance.IsHost() && item.NetId == 0 && item.gameObject.activeInHierarchy)
            {
                bool awaitingFirstInteraction = item.UnboundState == ClientItemUnboundState.None;
                issues.Add(new IdentityIssue
                {
                    Code = awaitingFirstInteraction ? "zero-id-awaiting-first-interaction" : "active-zero-id",
                    EntityId = "0",
                    Severity = awaitingFirstInteraction ? DebugSeverity.Warning : DebugSeverity.Error,
                    Detail = $"{item.name}#{item.gameObject.GetInstanceID()} {item.UnboundState}"
                });
            }
            else if (item.NetId != 0 && (!NetworkedItem.TryGet(item.NetId, out NetworkedItem indexed) || indexed != item))
                issues.Add(new IdentityIssue { Code = "lookup-mismatch", EntityId = item.NetId.ToString(), Severity = DebugSeverity.Error, Detail = $"live={item.name}#{item.gameObject.GetInstanceID()} indexed={indexed?.name ?? "null"}" });
        }
        try
        {
            itemIndexField ??= typeof(IdMonoBehaviour<ushort, NetworkedItem>).GetField("indexToObject", BindingFlags.Static | BindingFlags.NonPublic);
            if (itemIndexField?.GetValue(null) is IDictionary index)
            {
                var byInstance = new Dictionary<int, List<string>>();
                foreach (DictionaryEntry entry in index)
                {
                    if (entry.Value is not NetworkedItem value || value == null) continue;
                    int instance = value.gameObject.GetInstanceID();
                    if (!byInstance.TryGetValue(instance, out List<string> ids)) byInstance[instance] = ids = new();
                    ids.Add(Convert.ToString(entry.Key));
                }
                foreach (var pair in byInstance.Where(pair => pair.Value.Distinct().Count() > 1))
                    issues.Add(new IdentityIssue { Code = "stale-id-alias", EntityId = string.Join(",", pair.Value), Severity = DebugSeverity.Error, Detail = $"Unity instance {pair.Key} is indexed by multiple IDs" });
            }
        }
        catch (Exception exception) { issues.Add(new IdentityIssue { Code = "audit-failed", Severity = DebugSeverity.Warning, Detail = exception.Message }); }
        lock (gate) { identityIssues.Clear(); identityIssues.AddRange(issues.Take(100)); }
        IdentityIssue critical = issues.FirstOrDefault(item => item.Severity >= DebugSeverity.Error);
        if (critical != null) RequestAutoCapture($"identity-{critical.Code}-{critical.EntityId}");
    }

    private static void RequestAutoCapture(string trigger)
    {
        if (!AutomaticCapturesEnabled || string.IsNullOrWhiteSpace(trigger)) return;
        float now = Time.unscaledTime;
        lock (gate)
        {
            if (autoTriggerCooldowns.TryGetValue(trigger, out float allowedAt) && now < allowedAt) return;
            autoTriggerCooldowns[trigger] = now + 300f;
            pendingAutoTrigger ??= trigger;
        }
    }

    private static MutableQueue GetQueue(string kind, string entityId)
    {
        kind ??= string.Empty; entityId ??= string.Empty;
        if (!queues.TryGetValue(kind, out Dictionary<string, MutableQueue> entities)) queues[kind] = entities = new(StringComparer.OrdinalIgnoreCase);
        if (!entities.TryGetValue(entityId, out MutableQueue item))
        {
            entities[entityId] = item = new MutableQueue();
            item.Value.Key = $"{kind}:{entityId}"; item.Value.Kind = kind; item.Value.EntityId = entityId;
        }
        return item;
    }

    private static string Sanitize(string value) => new string((value ?? "trigger").Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').Take(60).ToArray());
    private static QueueHealth Copy(QueueHealth value) => new()
    {
        Key = value.Key, Kind = value.Kind, EntityId = value.EntityId, Depth = value.Depth, MaximumDepth = value.MaximumDepth,
        LastReceivedTick = value.LastReceivedTick, LastAppliedTick = value.LastAppliedTick, Received = value.Received,
        Applied = value.Applied, StaleDiscarded = value.StaleDiscarded, Cleared = value.Cleared, TickGaps = value.TickGaps,
        LargestTickGap = value.LargestTickGap, OldestUtc = value.OldestUtc, UpdatedUtc = value.UpdatedUtc
    };
    private static DependencyHealth Copy(DependencyHealth value) => new()
    {
        Key = value.Key, EntityType = value.EntityType, EntityId = value.EntityId, Reason = value.Reason, Detail = value.Detail,
        Count = value.Count, FirstSeenUtc = value.FirstSeenUtc, LastSeenUtc = value.LastSeenUtc
    };
    private static IdentityIssue Copy(IdentityIssue value) => new() { Code = value.Code, EntityId = value.EntityId, Detail = value.Detail, Severity = value.Severity };
    private static NetworkHealth Copy(NetworkHealth value) => new()
    {
        TickIntervalAverageMs = value.TickIntervalAverageMs, TickJitterAverageMs = value.TickJitterAverageMs,
        TickJitterMaximumMs = value.TickJitterMaximumMs, ObservabilityAverageMs = value.ObservabilityAverageMs,
        ObservabilityMaximumMs = value.ObservabilityMaximumMs, FrameAverageMs = value.FrameAverageMs,
        FrameMaximumMs = value.FrameMaximumMs, EventsPerSecond = value.EventsPerSecond,
        PacketsInPerSecond = value.PacketsInPerSecond, PacketsOutPerSecond = value.PacketsOutPerSecond,
        BytesInPerSecond = value.BytesInPerSecond, BytesOutPerSecond = value.BytesOutPerSecond,
        LatencyLatestMs = value.LatencyLatestMs, LatencyAverageMs = value.LatencyAverageMs,
        LatencyJitterMs = value.LatencyJitterMs, LatencyMaximumMs = value.LatencyMaximumMs,
        AutoCaptureCount = value.AutoCaptureCount, LastAutoTrigger = value.LastAutoTrigger
    };

    private sealed class MeasureScope : IDisposable
    {
        private readonly long startedAt = Stopwatch.GetTimestamp();
        private readonly string name;
        public MeasureScope(string name) => this.name = name;
        public void Dispose()
        {
            double milliseconds = (Stopwatch.GetTimestamp() - startedAt) * 1000d / Stopwatch.Frequency;
            observabilityTotal += milliseconds; observabilitySamples++; if (milliseconds > observabilityMax) observabilityMax = milliseconds;
        }
    }
    private sealed class EmptyScope : IDisposable { public static readonly EmptyScope Instance = new(); public void Dispose() { } }
}
