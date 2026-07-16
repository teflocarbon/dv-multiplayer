using DV;
using Multiplayer.Components.Networking;
using Multiplayer.Debugging.Protocol;
#if DEBUG
using Multiplayer.Debugging.RuntimeTests;
#endif
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Multiplayer.Debugging;

public static class DebugRuntime
{
    private static readonly object gate = new();
    private static DebugSessionInfo session;
    private static DebugEventStore store;
    private static AsyncJsonlSink fileSink;
    private static DebugHttpServer httpServer;
    private static DebugDiscoveryFile discovery;
    private static DebugCaptureManager captures;
    private static GameObject runtimeObject;
    private static DebugRuntimeSettingsDto runtimeSettings;
    private static int highFrequencyCounter;

    public static bool Enabled { get; private set; }
    public static DebugEventStore Store => store;
    public static DebugSessionInfo Session => SnapshotSession();
    public static DebugCaptureManager Captures => captures;
    public static DebugRuntimeSettingsDto RuntimeSettings => runtimeSettings;

    public static void ApplySettings(Settings settings)
    {
        if (settings?.EnableDebugSystem == true)
        {
            if (!Enabled) Start(settings); else UpdateSettings(settings);
        }
        else if (Enabled) Stop();
    }

    private static void Start(Settings settings)
    {
        lock (gate)
        {
            if (Enabled) return;
            Process process = Process.GetCurrentProcess();
            DateTime started = Multiplayer.SessionStartedUtc;
            string sessionId = Multiplayer.ProcessSessionId;
            string root = Path.Combine(Multiplayer.ModEntry.Path, "Multiplayer.Debug");
            Directory.CreateDirectory(root);
            session = new DebugSessionInfo
            {
                SessionId = sessionId,
                ProcessId = process.Id,
                ProcessStartedUtc = process.StartTime.ToUniversalTime(),
                StartedUtc = started,
                HeartbeatUtc = started,
                ApiToken = Guid.NewGuid().ToString("N"),
                BuildNumber = Multiplayer.BuildNumber,
                GameBuild = Multiplayer.LocalBuildInfo,
                ModCommit = GetCommit(),
                LogPath = Path.Combine(root, $"dvmp-debug-{sessionId}.jsonl")
            };
            // Raw capture is deliberately session-only. A persisted legacy setting can
            // otherwise copy and Base64-encode every sampled datagram as soon as both game
            // processes load, before the developer has a chance to open the overlay.
            if (settings.EnableRawPacketCapture)
            {
                settings.EnableRawPacketCapture = false;
                Multiplayer.LogWarning("Raw packet capture was disabled at debug-session startup; enable Raw explicitly from the packet inspector when needed.");
            }
            runtimeSettings = CreateRuntimeSettings(settings);
            store = new DebugEventStore(settings.DebugMaxInMemoryEvents);
            captures = new DebugCaptureManager(store, SnapshotSession, root);
            DebugDiagnostics.Start(store);
            EntityDebugRegistry.Start(store, settings.DebugMaxEntityTimelineEvents);
            if (settings.EnableDebugFileLogging) fileSink = new AsyncJsonlSink(store, session.LogPath);
            discovery = new DebugDiscoveryFile(root, SnapshotSession);
            runtimeObject = new GameObject("[Multiplayer Debug Runtime]");
            runtimeObject.AddComponent<DebugRuntimeBehaviour>();
#if DEBUG
            if (settings.EnableRuntimeTestHarness) runtimeObject.AddComponent<RuntimeTestAgent>();
#endif
            Object.DontDestroyOnLoad(runtimeObject);
            if (settings.EnableDebugFirehose) StartHttpServer();
            Settings.OnSettingsUpdated += ApplySettings;
            Enabled = true;
            Publish("session", "session.started", DebugRuntimeSide.Shared, DebugSeverity.Info, data: DebugValueSnapshotter.SnapshotObject(session));
        }
    }

    private static void UpdateSettings(Settings settings)
    {
        // Preserve the packet inspector's session-local trace selection while applying
        // ordinary UMM settings changes. Only sampling is persistent/safe to update here.
        if (runtimeSettings != null)
            runtimeSettings.HighFrequencySampling = Mathf.Clamp(settings.DebugHighFrequencySampling, 1, 120);
        store?.Resize(settings.DebugMaxInMemoryEvents);
        EntityDebugRegistry.Resize(settings.DebugMaxEntityTimelineEvents);
        DebugWorldLabelManager.SetEnabled(settings.EnableDebugWorldLabels);
#if DEBUG
        RuntimeTestAgent agent = runtimeObject == null ? null : runtimeObject.GetComponent<RuntimeTestAgent>();
        if (settings.EnableRuntimeTestHarness && agent == null) agent = runtimeObject.AddComponent<RuntimeTestAgent>();
        else if (!settings.EnableRuntimeTestHarness && agent != null) Object.Destroy(agent);
        ConfigureRuntimeTests(settings.EnableRuntimeTestHarness ? agent : null);
#endif
        if (settings.EnableDebugFileLogging && fileSink == null) fileSink = new AsyncJsonlSink(store, session.LogPath);
        else if (!settings.EnableDebugFileLogging && fileSink != null) { fileSink.Dispose(); fileSink = null; }
        if (settings.EnableDebugFirehose && httpServer == null) StartHttpServer();
        else if (!settings.EnableDebugFirehose && httpServer != null)
        {
            httpServer.Dispose(); httpServer = null;
            session.FirehosePort = 0; session.FirehoseUrl = string.Empty;
            discovery?.Write();
        }
    }

    private static void StartHttpServer()
    {
        try
        {
            httpServer = new DebugHttpServer(store, SnapshotSession, EntityDebugRegistry.Snapshot, EntityDebugRegistry.Get,
                () => runtimeSettings, ApplyRuntimeSettings, Mark, (name, id) => StartCapture(name, id), StopCapture);
#if DEBUG
            ConfigureRuntimeTests(runtimeObject == null ? null : runtimeObject.GetComponent<RuntimeTestAgent>());
#endif
            httpServer.Start();
            session.FirehosePort = httpServer.Port;
            session.FirehoseUrl = httpServer.Url;
            discovery?.Write();
        }
        catch (Exception exception) { httpServer?.Dispose(); httpServer = null; Multiplayer.LogWarning($"Debug firehose unavailable: {exception.Message}"); }
    }

#if DEBUG
    private static void ConfigureRuntimeTests(RuntimeTestAgent agent)
    {
        httpServer?.ConfigureRuntimeTests(agent == null ? null : agent.GetCapabilities,
            agent == null ? null : agent.GetRuns, agent == null ? null : agent.Enqueue,
            agent == null ? null : agent.GetRun,
            agent == null ? null : agent.Cancel);
    }
#endif

    public static void Stop()
    {
        lock (gate)
        {
            if (!Enabled && store == null) return;
            if (Enabled) Publish("session", "session.stopping", DebugRuntimeSide.Shared, DebugSeverity.Info);
            Enabled = false;
            Settings.OnSettingsUpdated -= ApplySettings;
            DebugDiagnostics.Stop();
            EntityDebugRegistry.Stop();
            captures?.Dispose(); captures = null;
            discovery?.Dispose(); discovery = null;
            httpServer?.Dispose(); httpServer = null;
            fileSink?.Dispose(); fileSink = null;
            if (runtimeObject != null) Object.Destroy(runtimeObject);
            runtimeObject = null;
            store = null;
            session = null;
        }
    }

    public static bool EnabledFor(string category)
    {
        if (!Enabled || runtimeSettings == null) return false;
        return runtimeSettings.Categories.Length == 0 || runtimeSettings.Categories.Contains(category, StringComparer.OrdinalIgnoreCase);
    }

    public static bool ShouldEmitHighFrequency(string entityType = "", string entityId = "")
    {
        if (!Enabled || runtimeSettings == null) return false;
        if (runtimeSettings.HighFrequencySampling <= 1 || EntityDebugRegistry.IsTraced(entityType, entityId)) return true;
        return ++highFrequencyCounter % runtimeSettings.HighFrequencySampling == 0;
    }

    public static DebugEvent Publish(string category, string eventName, DebugRuntimeSide side, DebugSeverity severity = DebugSeverity.Info,
        string entityType = "", string entityId = "", Dictionary<string, object> data = null, bool highFrequency = false,
        string correlationId = "", string causationId = "", bool samplingDecided = false)
    {
        if (!EnabledFor(category) || store == null) return null;
        if (highFrequency && !samplingDecided && !ShouldEmitHighFrequency(entityType, entityId)) return null;
        DebugSessionInfo info = session;
        DebugEvent item = new()
        {
            TimestampUtc = DateTime.UtcNow,
            Frame = Time.frameCount,
            NetworkTick = NetworkLifecycle.Instance != null ? NetworkLifecycle.Instance.Tick : null,
            SessionId = info.SessionId,
            ProcessId = info.ProcessId,
            Role = info.Role,
            RuntimeSide = side,
            LocalPlayerId = info.PlayerId,
            Category = category,
            EventName = eventName,
            Severity = severity,
            EntityType = entityType ?? string.Empty,
            EntityId = entityId ?? string.Empty,
            CorrelationId = correlationId ?? string.Empty,
            CausationId = causationId ?? string.Empty,
            HighFrequency = highFrequency,
            Data = data ?? new Dictionary<string, object>(StringComparer.Ordinal)
        };
#if DEBUG
        RuntimeTestScopeSnapshot testScope = RuntimeTestScope.Snapshot;
        item.TestRunId = testScope.RunId;
        item.TestCaseId = testScope.CaseId;
        item.TestPhaseId = testScope.PhaseId;
        item.TestStepId = testScope.StepId;
#endif
        store.Publish(item);
        EntityDebugRegistry.Observe(item);
        return item;
    }

    public static DebugEvent PublishHighFrequency(string category, string eventName, DebugRuntimeSide side,
        string entityType, string entityId, Func<Dictionary<string, object>> data)
    {
        if (!EnabledFor(category) || !ShouldEmitHighFrequency(entityType, entityId)) return null;
        Dictionary<string, object> snapshot;
        try { snapshot = data?.Invoke() ?? new(); }
        catch (Exception exception) { snapshot = new() { ["snapshotError"] = exception.Message }; }
        return Publish(category, eventName, side, entityType: entityType, entityId: entityId,
            data: snapshot, highFrequency: true, samplingDecided: true);
    }

    public static void SetRole(string role, string playerName = null, byte? playerId = null)
    {
        if (!Enabled || session == null) return;
        session.Role = role ?? session.Role;
        if (playerName != null) session.PlayerName = playerName;
        if (playerId.HasValue) session.PlayerId = playerId;
        EntityDebugRegistry.RegisterLocalPlayer();
        discovery?.Write();
        Publish("session", "session.identity-updated", DebugRuntimeSide.Shared, data: DebugValueSnapshotter.SnapshotObject(session));
    }

    public static void Mark(string text) => Publish("marker", "marker.created", DebugRuntimeSide.Shared, data: new() { ["text"] = text ?? string.Empty });
    public static string StartCapture(string name, string captureId = null)
    {
        string path = captures?.Start(name, captureId) ?? string.Empty;
        Publish("capture", "capture.started", DebugRuntimeSide.Shared, data: new() { ["name"] = name ?? "capture", ["path"] = path, ["captureId"] = captures?.CaptureId ?? string.Empty });
        return path;
    }
    public static string StopCapture()
    {
        string path = captures?.CurrentPath ?? string.Empty;
        Publish("capture", "capture.stopped", DebugRuntimeSide.Shared, data: new() { ["path"] = path });
        captures?.Stop();
        return path;
    }

    public static void Clear() => store?.Clear();

    private static DebugSessionInfo SnapshotSession()
    {
        if (session == null) return null;
        return new DebugSessionInfo
        {
            SchemaVersion = session.SchemaVersion, SessionId = session.SessionId, ProcessId = session.ProcessId,
            ProcessStartedUtc = session.ProcessStartedUtc, StartedUtc = session.StartedUtc, HeartbeatUtc = DateTime.UtcNow,
            Role = session.Role, PlayerName = session.PlayerName, PlayerId = session.PlayerId,
            FirehosePort = session.FirehosePort, FirehoseUrl = session.FirehoseUrl, ApiToken = session.ApiToken,
            LogPath = session.LogPath, BuildNumber = session.BuildNumber,
            GameBuild = session.GameBuild, ModCommit = session.ModCommit
        };
    }

    private static DebugRuntimeSettingsDto CreateRuntimeSettings(Settings settings) => new()
    {
        TraceMode = DebugTraceMode.Summary,
        RawPacketCapture = false,
        HighFrequencySampling = settings.DebugHighFrequencySampling,
        Categories = Array.Empty<string>()
    };

    private static void ApplyRuntimeSettings(DebugRuntimeSettingsDto value)
    {
        if (value == null) return;
        value.HighFrequencySampling = Mathf.Clamp(value.HighFrequencySampling, 1, 120);
        if (value.TraceMode == DebugTraceMode.Raw) value.RawPacketCapture = true;
        runtimeSettings = value;
        Publish("session", "debug.settings-updated", DebugRuntimeSide.Shared, data: DebugValueSnapshotter.SnapshotObject(value));
    }

    private static string GetCommit()
    {
        AssemblyMetadataAttribute metadata = typeof(Multiplayer).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "RepositoryCommit");
        return string.IsNullOrWhiteSpace(metadata?.Value) ? "unknown" : metadata.Value;
    }
}

internal sealed class DebugRuntimeBehaviour : MonoBehaviour
{
    private WorldMover worldMover;
    private NetworkLifecycle networkLifecycle;
    private Vector3 beforeMove;
    private int originShiftCount;

    private void Update()
    {
        long frameStarted = Stopwatch.GetTimestamp();
        if (Input.GetKeyDown(KeyCode.F6)) DebugOverlayController.ToggleSettings();
        if (Input.GetKeyDown(KeyCode.F7)) DebugOverlayController.ToggleSize();
        if (Input.GetKeyDown(KeyCode.F8)) DebugOverlayController.Toggle();
        if (Input.GetKeyDown(KeyCode.F9)) DebugWorldLabelManager.SetEnabled(!DebugWorldLabelManager.Enabled);
        if (Input.GetKeyDown(KeyCode.F10)) DebugOverlayController.ToggleFreeze();
        if (Input.GetKeyDown(KeyCode.F11)) DebugOverlayController.ToggleVerbose();
        if (Input.GetKeyDown(KeyCode.F12)) DebugOverlayController.SelectLookedAt();
        DebugOverlayController.HandleKeyboard();
        if (worldMover == null && WorldMover.Instance != null)
        {
            worldMover = WorldMover.Instance;
            worldMover.AboutToMoveWorld += OnAboutToMoveWorld;
            worldMover.WorldMoved += OnWorldMoved;
        }
        if (networkLifecycle == null && NetworkLifecycle.Instance != null)
        {
            networkLifecycle = NetworkLifecycle.Instance;
            networkLifecycle.OnTick += OnNetworkTick;
        }
        using (DebugDiagnostics.Measure("entity-registry")) EntityDebugRegistry.Tick();
        using (DebugDiagnostics.Measure("world-labels")) DebugWorldLabelManager.Tick();
        using (DebugDiagnostics.Measure("desync-detector")) DebugDesyncDetector.Tick();
        using (DebugDiagnostics.Measure("diagnostics")) DebugDiagnostics.Tick();
        DebugDiagnostics.RecordFrame((float)((Stopwatch.GetTimestamp() - frameStarted) * 1000d / Stopwatch.Frequency));
    }

    private void Awake()
    {
        gameObject.AddComponent<DebugOverlayController>();
        gameObject.AddComponent<DebugInventoryObserver>();
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
    }

    private void OnAboutToMoveWorld(Vector3 previous, Vector3 next)
    {
        beforeMove = WorldMover.currentMove;
        DebugRuntime.Publish("world", "world.origin-shift.before", DebugRuntimeSide.Shared, data: WorldDebugState(previous, next));
    }

    private void OnWorldMoved(WorldMover mover, Vector3 vector)
    {
        Dictionary<string, object> data = WorldDebugState(beforeMove, WorldMover.currentMove);
        data["moveVector"] = DebugValueSnapshotter.Snapshot(vector);
        data["originShiftCount"] = ++originShiftCount;
        DebugRuntime.Publish("world", "world.origin-shift.after", DebugRuntimeSide.Shared, data: data);
    }

    private static Dictionary<string, object> WorldDebugState(Vector3 previous, Vector3 next)
    {
        Dictionary<string, object> data = new()
        {
            ["previousMove"] = DebugValueSnapshotter.Snapshot(previous),
            ["currentMove"] = DebugValueSnapshotter.Snapshot(next),
            ["originShiftParentPosition"] = DebugValueSnapshotter.Snapshot(WorldMover.OriginShiftParent?.position)
        };
        if (PlayerManager.PlayerTransform != null)
        {
            data["playerLocalPosition"] = DebugValueSnapshotter.Snapshot(PlayerManager.PlayerTransform.position);
            data["playerAbsolutePosition"] = DebugValueSnapshotter.Snapshot(PlayerManager.PlayerTransform.position - WorldMover.currentMove);
            data["playerCar"] = PlayerManager.Car?.ID ?? string.Empty;
        }
        return data;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => DebugRuntime.Publish("scene", "scene.loaded", DebugRuntimeSide.Shared, data: new() { ["name"] = scene.name, ["buildIndex"] = scene.buildIndex, ["mode"] = mode.ToString() });
    private static void OnSceneUnloaded(Scene scene) => DebugRuntime.Publish("scene", "scene.unloaded", DebugRuntimeSide.Shared, data: new() { ["name"] = scene.name, ["buildIndex"] = scene.buildIndex });
    private static void OnNetworkTick(uint tick) => DebugDiagnostics.RecordNetworkTick(tick);

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        if (worldMover != null) { worldMover.AboutToMoveWorld -= OnAboutToMoveWorld; worldMover.WorldMoved -= OnWorldMoved; }
        if (networkLifecycle != null) networkLifecycle.OnTick -= OnNetworkTick;
    }

    private void OnApplicationQuit() => DebugRuntime.Stop();
}
