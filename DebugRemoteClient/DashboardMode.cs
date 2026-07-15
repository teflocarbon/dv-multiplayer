using Multiplayer.Debugging.Protocol;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplayer.DebugClient;

internal static class DashboardMode
{
    public static int Run(string requestedRoot)
    {
        string root = ResolveRoot(requestedRoot);
        Console.WriteLine($"Scanning debug sessions under: {root}");
        DebugEventStore store = new(50000);
        int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        DebugSessionInfo dashboard = new()
        {
            SessionId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-p{pid}-{Guid.NewGuid():N}".Substring(0, 31),
            ProcessId = pid,
            ProcessStartedUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            StartedUtc = DateTime.UtcNow,
            HeartbeatUtc = DateTime.UtcNow,
            Role = "dashboard",
            ApiToken = Guid.NewGuid().ToString("N")
        };
        DebugRuntimeSettingsDto dashboardSettings = new();
        using DashboardMerger merger = new(root, store);
        using DebugHttpServer server = new(store, () => dashboard,
            settings: () => dashboardSettings,
            updateSettings: value => { dashboardSettings = value ?? new DebugRuntimeSettingsDto(); merger.UpdateSettings(dashboardSettings); },
            mark: merger.Mark,
            startCapture: merger.StartCapture,
            stopCapture: merger.StopCapture,
            sessions: merger.Sessions,
            replicationOperations: merger.ReplicationOperations,
            replicationOperation: merger.ReplicationOperation);
        server.Start();
        dashboard.FirehosePort = server.Port;
        dashboard.FirehoseUrl = server.Url;
        merger.Start();
        Console.WriteLine($"Combined dashboard: {server.Url}");
        Console.WriteLine("All live sessions are merged automatically. Type /quit to stop.");
        while (true)
        {
            string command = Console.ReadLine();
            if (command == null || string.Equals(command, "/quit", StringComparison.OrdinalIgnoreCase)) break;
        }
        return 0;
    }

    private static string ResolveRoot(string requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return Path.GetFullPath(requested);
        string standard = @"C:\Program Files (x86)\Steam\steamapps\common\Derail Valley\Mods\Multiplayer\Multiplayer.Debug";
        if (Directory.Exists(standard)) return standard;
        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Multiplayer.Debug"));
    }
}

internal sealed class DashboardMerger : IDisposable
{
    private readonly string root;
    private readonly DebugEventStore store;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentDictionary<string, Task> readers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DebugSessionInfo> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<DebugEvent> pending = new();
    private readonly ReplicationCoordinator replication = new();
    private Task scanTask;
    private Task flushTask;
    private DateTime nextAutomaticCaptureUtc;
    private int automaticCaptureCount;

    public DashboardMerger(string root, DebugEventStore store) { this.root = root; this.store = store; replication.DiscontinuityDetected += OnDiscontinuity; }
    public IEnumerable<DebugSessionInfo> Sessions() => sessions.Values.OrderBy(item => item.Role).ThenBy(item => item.ProcessId).ToArray();
    public IEnumerable<ReplicationOperationSummaryDto> ReplicationOperations() => replication.SnapshotSummaries();
    public ReplicationOperationDto ReplicationOperation(string id) => replication.Get(id);
    public void Mark(string text) => Broadcast("api/mark", new { text });
    public void UpdateSettings(DebugRuntimeSettingsDto value)
    {
        value ??= new DebugRuntimeSettingsDto();
        DashboardAutomaticCapturesEnabled = value.AutomaticReplicationCaptures;
        Broadcast("api/settings", value);
    }
    public string StartCapture(string name, string captureId)
    {
        string id = string.IsNullOrWhiteSpace(captureId) ? Guid.NewGuid().ToString("N") : captureId;
        Broadcast("api/capture/start", new { name = string.IsNullOrWhiteSpace(name) ? "dashboard-capture" : name, captureId = id });
        return id;
    }
    public string StopCapture() { Broadcast("api/capture/stop", new { }); return "stopped"; }

    private void Broadcast(string relativePath, object body)
    {
        byte[] content = Encoding.UTF8.GetBytes(DebugJson.Serialize(body));
        foreach (DebugSessionInfo info in sessions.Values.ToArray())
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(new Uri(new Uri(info.FirehoseUrl), relativePath));
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Headers["X-DVMP-Debug-Token"] = info.ApiToken;
                request.ContentLength = content.Length;
                using (Stream stream = request.GetRequestStream()) stream.Write(content, 0, content.Length);
                using WebResponse response = request.GetResponse();
            }
            catch { }
        }
    }

    public void Start()
    {
        scanTask = Task.Run(ScanLoop);
        flushTask = Task.Run(FlushLoop);
    }

    private async Task ScanLoop()
    {
        string directory = Path.Combine(root, "sessions");
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                if (Directory.Exists(directory))
                    foreach (string file in Directory.GetFiles(directory, "*.json"))
                    {
                        DebugSessionInfo info;
                        try { info = JsonConvert.DeserializeObject<DebugSessionInfo>(File.ReadAllText(file), DebugJson.Settings); }
                        catch { continue; }
                        if (!DebugDiscoveryFile.IsLive(info, DateTime.UtcNow, TimeSpan.FromSeconds(10))) { sessions.TryRemove(info?.SessionId ?? string.Empty, out _); continue; }
                        sessions[info.SessionId] = info;
                        replication.UpdateSessions(sessions.Values);
                        Task readerTask = readers.GetOrAdd(info.SessionId, _ => Task.Run(() => ReadSession(info)));
                    }
            }
            catch { }
            await Task.Delay(2000, cancellation.Token).ConfigureAwait(false);
        }
    }

    private void ReadSession(DebugSessionInfo info)
    {
        HttpWebRequest request = null;
        try
        {
            request = (HttpWebRequest)WebRequest.Create(new Uri(new Uri(info.FirehoseUrl), "events"));
            request.Timeout = Timeout.Infinite;
            request.ReadWriteTimeout = Timeout.Infinite;
            using WebResponse response = request.GetResponse();
            using StreamReader reader = new(response.GetResponseStream(), Encoding.UTF8);
            while (!cancellation.IsCancellationRequested)
            {
                string line = reader.ReadLine();
                if (line == null) break;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                try
                {
                    DebugEvent item = JsonConvert.DeserializeObject<DebugEvent>(line.Substring(6), DebugJson.Settings);
                    if (item == null) continue;
                    item.Data ??= new Dictionary<string, object>(StringComparer.Ordinal);
                    item.SourceSequence = item.SourceSequence == 0 ? item.Sequence : item.SourceSequence;
                    item.EventKey = string.IsNullOrEmpty(item.EventKey) ? $"{item.SessionId}:{item.SourceSequence}" : item.EventKey;
                    pending.Enqueue(item);
                }
                catch { }
            }
        }
        catch { }
        finally { request?.Abort(); readers.TryRemove(info.SessionId, out _); }
    }

    private async Task FlushLoop()
    {
        List<DebugEvent> buffered = new();
        while (!cancellation.IsCancellationRequested)
        {
            while (pending.TryDequeue(out DebugEvent item)) buffered.Add(item);
            DateTime cutoff = DateTime.UtcNow.AddMilliseconds(-250);
            DebugEvent[] ready = buffered.Where(item => item.TimestampUtc <= cutoff).OrderBy(item => item.TimestampUtc).ThenBy(item => item.SessionId).ThenBy(item => item.Sequence).ToArray();
            foreach (DebugEvent item in ready)
            {
                buffered.Remove(item);
                sessions.TryGetValue(item.SessionId, out DebugSessionInfo session);
                replication.Observe(session, item);
                store.Publish(item);
            }
            replication.Tick(DateTime.UtcNow);
            await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
        }
    }

    private void OnDiscontinuity(ReplicationOperationDto operation)
    {
        store.Publish(new DebugEvent
        {
            TimestampUtc = DateTime.UtcNow, SessionId = "replication-coordinator", Role = "dashboard", RuntimeSide = DebugRuntimeSide.Dashboard,
            Category = "replication", EventName = "replication.discontinuity-detected", Severity = DebugSeverity.Error,
            EntityType = "Item", EntityId = operation.EntityId,
            Data = new Dictionary<string, object>
            {
                ["operationId"] = operation.OperationId, ["updateType"] = operation.UpdateType,
                ["reason"] = operation.DiscontinuityReason, ["correlationConfidence"] = operation.CorrelationConfidence,
                ["recipients"] = operation.Recipients.Select(recipient => new { recipient.PlayerId, recipient.PlayerName, recipient.Sent, recipient.Received, recipient.Handled, recipient.Applied, recipient.AcknowledgedWithoutApply, recipient.Discontinuity }).ToArray()
            }
        });
        if (!DashboardAutomaticCapturesEnabled || automaticCaptureCount >= 10 || DateTime.UtcNow < nextAutomaticCaptureUtc) return;
        automaticCaptureCount++; nextAutomaticCaptureUtc = DateTime.UtcNow.AddSeconds(15);
        string captureId = Guid.NewGuid().ToString("N");
        Broadcast("api/capture/start", new { name = $"replication-{operation.EntityId}-{operation.DiscontinuityReason}", captureId });
        Broadcast("api/mark", new { text = $"REPLICATION DISCONTINUITY {operation.OperationId}: Item {operation.EntityId} {operation.DiscontinuityReason}" });
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000).ConfigureAwait(false);
            Broadcast("api/capture/stop", new { });
        });
    }

    public bool DashboardAutomaticCapturesEnabled { get; set; }

    public void Dispose()
    {
        cancellation.Cancel();
        replication.DiscontinuityDetected -= OnDiscontinuity;
        try { Task.WaitAll(new[] { scanTask, flushTask }.Where(task => task != null).ToArray(), 1000); } catch { }
        cancellation.Dispose();
    }
}
