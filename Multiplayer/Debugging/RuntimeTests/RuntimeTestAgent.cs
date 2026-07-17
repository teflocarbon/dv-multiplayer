#if DEBUG
using DV.Interaction;
using DV.Common;
using DV.InventorySystem;
using DV.UI;
using DV.UI.PresetEditors;
using Multiplayer.API;
using Multiplayer.Components.Networking;
using Multiplayer.Components.UI.ServerBrowser;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data;
using Multiplayer.Patches.MainMenu;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Multiplayer.Debugging.RuntimeTests;

internal readonly struct RuntimeTestScopeSnapshot
{
    public readonly string RunId;
    public readonly string CaseId;
    public readonly string PhaseId;
    public readonly string StepId;

    public RuntimeTestScopeSnapshot(RuntimeTestCommandDto command)
    {
        RunId = command?.RunId ?? string.Empty;
        CaseId = command?.CaseId ?? string.Empty;
        PhaseId = command?.PhaseId ?? string.Empty;
        StepId = command?.StepId ?? string.Empty;
    }

    public RuntimeTestScopeSnapshot(RuntimeTestScopeSnapshot source, string phaseId,
        string stepId)
    {
        RunId = source.RunId;
        CaseId = source.CaseId;
        PhaseId = phaseId ?? string.Empty;
        StepId = stepId ?? string.Empty;
    }
}

internal static class RuntimeTestScope
{
    private static readonly object gate = new();
    private static RuntimeTestScopeSnapshot current;

    public static RuntimeTestScopeSnapshot Snapshot { get { lock (gate) return current; } }
    public static void Set(RuntimeTestCommandDto command) { lock (gate) current = new RuntimeTestScopeSnapshot(command); }
    public static void Update(string phaseId, string stepId)
    {
        lock (gate) current = new RuntimeTestScopeSnapshot(current, phaseId, stepId);
    }
    public static void Clear() { lock (gate) current = default; }
}

internal sealed class RuntimeTestAgent : MonoBehaviour
{
    private readonly ConcurrentQueue<RuntimeTestCommandDto> queued = new();
    private readonly ConcurrentDictionary<string, RuntimeTestRunDto> runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> cancelled = new(StringComparer.Ordinal);
    private RuntimeTestCapabilitiesDto cachedCapabilities = UnavailableCapabilities();
    private readonly DerailValleyItemTestDriver itemDriver = new();
    private readonly InventoryRuntimeFixtureDriver inventoryFixtures = new();
    private Coroutine active;
    private float nextCapabilityRefresh;
    private bool sceneAnchorsDirty = true;
    private JobValidator cachedJobValidator;

    public static RuntimeTestAgent Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        SceneManager.sceneLoaded += OnSceneLoaded;
        RefreshCapabilities();
    }

    private void Update()
    {
        if (Time.unscaledTime >= nextCapabilityRefresh) RefreshCapabilities();
        if (active == null && queued.TryDequeue(out RuntimeTestCommandDto command))
            active = StartCoroutine(Execute(command));
    }

    public RuntimeTestCapabilitiesDto GetCapabilities()
    {
        RuntimeTestCapabilitiesDto source = cachedCapabilities;
        return new RuntimeTestCapabilitiesDto
        {
            SchemaVersion = source.SchemaVersion,
            Available = source.Available,
            MainThreadAgentReady = source.MainThreadAgentReady,
            BuildConfiguration = source.BuildConfiguration,
            Role = source.Role,
            PlayerId = source.PlayerId,
            Scene = source.Scene,
            Capabilities = source.Capabilities.ToArray(),
            Commands = source.Commands.ToArray(),
            Tests = source.Tests.Select(CloneDescriptor).ToArray(),
            Anchors = new Dictionary<string, object>(source.Anchors, StringComparer.Ordinal)
        };
    }

    public RuntimeTestCommandAcceptedDto Enqueue(RuntimeTestCommandDto command)
    {
        string error = Validate(command);
        if (!string.IsNullOrEmpty(error)) return Rejected(command, error);
        if (queued.Count >= 128) return Rejected(command, "command-queue-full");
        command.RequestId = string.IsNullOrWhiteSpace(command.RequestId) ? Guid.NewGuid().ToString("N") : command.RequestId.Trim();
        command.RunId = string.IsNullOrWhiteSpace(command.RunId) ? "run-" + Guid.NewGuid().ToString("N") : command.RunId.Trim();
        command.TimeoutMilliseconds = Mathf.Clamp(command.TimeoutMilliseconds, 1000, 120000);
        command.Parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (runs.Count >= 512) PruneCompletedRuns();
        if (!runs.TryAdd(command.RequestId, NewRun(command))) return Rejected(command, "duplicate-request-id");
        queued.Enqueue(command);
        DebugRuntime.Publish("runtime-test", "runtime-test.queued", DebugRuntimeSide.Shared,
            correlationId: command.RunId, data: new() { ["requestId"] = command.RequestId, ["command"] = command.Command });
        return new RuntimeTestCommandAcceptedDto
        {
            RequestId = command.RequestId, RunId = command.RunId, Accepted = true,
            StatusUrl = "/api/runtime-tests/runs/" + Uri.EscapeDataString(command.RequestId)
        };
    }

    public RuntimeTestRunDto GetRun(string requestId) =>
        requestId != null && runs.TryGetValue(requestId, out RuntimeTestRunDto run) ? Clone(run) : null;

    public IEnumerable<RuntimeTestRunSummaryDto> GetRuns() => runs.Values
        .OrderByDescending(run => run.QueuedUtc)
        .Select(Summarize)
        .ToArray();

    public bool Cancel(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || !runs.TryGetValue(requestId, out RuntimeTestRunDto run) || IsTerminal(run.Status)) return false;
        cancelled[requestId] = 0;
        return true;
    }

    private IEnumerator Execute(RuntimeTestCommandDto command)
    {
        RuntimeTestRunDto run = runs[command.RequestId];
        lock (run)
        {
            run.Status = RuntimeTestCommandStatus.Running;
            run.StartedUtc = DateTime.UtcNow;
            run.Role = CurrentRole();
            run.PlayerId = LocalPlayerId();
            AddRuntimeContext(run);
        }
        RuntimeTestScope.Set(command);
        DebugRuntime.Publish("runtime-test", "runtime-test.started", DebugRuntimeSide.Shared,
            correlationId: command.RunId, data: new() { ["requestId"] = command.RequestId, ["command"] = command.Command });

        IEnumerator operation = command.Command switch
        {
            "runtime.self-check" => SelfCheck(run),
            "environment.status" => EnvironmentStatus(run),
            "environment.host-latest-save" => HostLatestSave(command, run),
            "environment.connect-client" => ConnectClient(command, run),
            "player.teleport" => Teleport(command, run),
            "item.pickup" => itemDriver.Pickup(command, run),
            "item.drop" => itemDriver.Drop(command, run),
            "item.throw" => itemDriver.Throw(command, run),
            "inventory.inspect" => inventoryFixtures.Inspect(command, run),
            "inventory.prefab-catalog" => inventoryFixtures.PrefabCatalog(command, run),
            "inventory.fixture-create" => inventoryFixtures.CreateFixture(command, run),
            "inventory.fixture-place" => inventoryFixtures.PlaceFixture(command, run),
            "inventory.fixture-destroy" => inventoryFixtures.DestroyFixture(command, run),
            "inventory.fixture-clean-local" => inventoryFixtures.CleanupLocalFixtures(command, run),
            "inventory.local-place" => inventoryFixtures.PlaceLocal(command, run),
            _ => RuntimeTestScenarioRegistry.CreateExecution(command.Command, command, run)
        };
        if (operation == null)
        {
            Complete(run, RuntimeTestCommandStatus.Unsupported, "unsupported-command");
            Finish(command, run);
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + command.TimeoutMilliseconds / 1000f;
        while (true)
        {
            if (cancelled.ContainsKey(command.RequestId))
            {
                (operation as IDisposable)?.Dispose();
                Complete(run, CleanupDirty(run) ? RuntimeTestCommandStatus.FailedDirty :
                    RuntimeTestCommandStatus.Cancelled, CleanupDirty(run) ?
                    "cancelled-with-incomplete-cleanup" : "cancelled");
                break;
            }
            if (Time.realtimeSinceStartup > deadline)
            {
                (operation as IDisposable)?.Dispose();
                Complete(run, CleanupDirty(run) ? RuntimeTestCommandStatus.FailedDirty :
                    RuntimeTestCommandStatus.Failed, CleanupDirty(run) ?
                    "timeout-with-incomplete-cleanup" : "timeout");
                break;
            }

            bool moved;
            object yielded = null;
            try
            {
                moved = operation.MoveNext();
                if (moved) yielded = operation.Current;
            }
            catch (Exception exception)
            {
                Exception root = exception.GetBaseException();
                Complete(run, root is RuntimeTestUnsupportedException ?
                    RuntimeTestCommandStatus.Unsupported : CleanupDirty(run) ?
                        RuntimeTestCommandStatus.FailedDirty : RuntimeTestCommandStatus.Failed,
                    root.Message);
                break;
            }
            if (!moved)
            {
                Complete(run, RuntimeTestCommandStatus.Passed, string.Empty);
                break;
            }
            yield return yielded;
        }
        (operation as IDisposable)?.Dispose();
        Finish(command, run);
    }

    private static IEnumerator SelfCheck(RuntimeTestRunDto run)
    {
        yield return null;
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        JobValidator[] validators = FindObjectsOfType<JobValidator>();
        lock (run)
        {
            run.Result["playerReady"] = PlayerManager.PlayerTransform != null;
            run.Result["worldMoverReady"] = WorldMover.Instance != null;
            run.Result["networkLifecycleReady"] = lifecycle != null;
            run.Result["serverRunning"] = lifecycle?.IsServerRunning == true;
            run.Result["clientRunning"] = lifecycle?.IsClientRunning == true;
            run.Result["activeScene"] = SceneManager.GetActiveScene().name;
            run.Result["jobValidatorCount"] = validators.Length;
            run.Result["playerAbsolutePosition"] = PlayerManager.PlayerTransform == null
                ? null : DebugValueSnapshotter.Snapshot(PlayerManager.PlayerTransform.position - WorldMover.currentMove);
        }
        if (PlayerManager.PlayerTransform == null) throw new InvalidOperationException("player-transform-unavailable");
        if (WorldMover.Instance == null) throw new InvalidOperationException("world-mover-unavailable");
    }

    private static IEnumerator EnvironmentStatus(RuntimeTestRunDto run)
    {
        yield return null;
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        bool itemsLoaded = StartingItemsController.Instance != null && StartingItemsController.Instance.itemsLoaded;
        lock (run)
        {
            run.Result["scene"] = SceneManager.GetActiveScene().name;
            run.Result["mainMenuReady"] = FindObjectOfType<MainMenuController>() != null;
            run.Result["playerReady"] = PlayerManager.PlayerTransform != null;
            run.Result["itemsLoaded"] = itemsLoaded;
            run.Result["serverRunning"] = lifecycle?.IsServerRunning == true;
            run.Result["clientRunning"] = lifecycle?.IsClientRunning == true;
            run.Result["networkLoadState"] = lifecycle?.Client?.LoadingState.ToString() ?? "None";
            run.Result["networkComplete"] = lifecycle?.Client?.LoadingState == Networking.Data.PlayerLoadingState.Complete;
        }
    }

    private static IEnumerator HostLatestSave(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance ?? throw new InvalidOperationException("network-lifecycle-unavailable");
        if (lifecycle.IsServerRunning || lifecycle.IsClientRunning) throw new InvalidOperationException("network-already-running");
        MainMenuController menu = FindObjectOfType<MainMenuController>();
        if (menu == null) throw new InvalidOperationException("main-menu-not-ready");
        Button button = menu.continueButton;
        if (button == null) throw new InvalidOperationException("continue-button-unavailable");

        int port = RequiredInt(command, "port");
        string password = OptionalString(command, "password", string.Empty);
        string serverName = OptionalString(command, "serverName", "DVMP automated test");
        int maxPlayers = Mathf.Clamp(OptionalInt(command, "maxPlayers", 2), 2, 20);
        ModInfo[] requiredMods = ModCompatibilityManager.Instance?.GetLocalMods();
        if (requiredMods == null) throw new InvalidOperationException("mod-compatibility-check-failed");

        Multiplayer.Settings.ServerName = serverName;
        Multiplayer.Settings.Password = password;
        Multiplayer.Settings.Port = port;
        Multiplayer.Settings.MaxPlayers = maxPlayers;
        Multiplayer.Settings.Visibility = ServerVisibility.Private;
        lifecycle.serverData = new LobbyServerData
        {
            port = port, Name = serverName, HasPassword = !string.IsNullOrEmpty(password), Visibility = ServerVisibility.Private,
            GameMode = 0, Difficulty = 0, TimePassed = "N/A", CurrentPlayers = 0, MaxPlayers = maxPlayers,
            RequiredMods = requiredMods, GameVersion = MainMenuControllerPatch.MenuProvider.BuildVersionString,
            MultiplayerVersion = Multiplayer.Ver, ServerDetails = "Automated runtime-test environment"
        };
        lifecycle.IsSinglePlayer = false;
        // Continue opens the session selector; loading does not begin until its selected
        // save is passed through the Launcher and the Launcher's Run action is invoked.
        button.onClick.Invoke();
        yield return null;
        yield return null;

        ContinueLoadNewController selector = menu.rightPaneController?.continueLoadNewController;
        if (selector == null) throw new InvalidOperationException("continue-session-selector-unavailable");
        ISaveGame careerSave = selector.career?.CurrentThing?.LatestSave;
        ISaveGame freeRoamSave = selector.freeRoam?.CurrentThing?.LatestSave;
        ISaveGame latestSave = new[] { careerSave, freeRoamSave }
            .Where(save => save != null)
            .OrderByDescending(save => save.Timestamp)
            .FirstOrDefault();
        if (latestSave == null) throw new InvalidOperationException("no-existing-save-found");

        MethodInfo continueSelected = typeof(MainMenuController).GetMethod("OnContinueGameRequested", BindingFlags.Instance | BindingFlags.NonPublic);
        if (continueSelected == null) throw new InvalidOperationException("continue-selected-save-action-unavailable");
        continueSelected.Invoke(menu, new object[] { latestSave });
        yield return null;
        yield return null;

        LauncherController launcher = FindObjectOfType<LauncherController>();
        MethodInfo runSelected = typeof(LauncherController).GetMethod("OnRunClicked", BindingFlags.Instance | BindingFlags.NonPublic);
        if (launcher == null || runSelected == null) throw new InvalidOperationException("save-launcher-unavailable");
        lock (run)
        {
            run.Result["port"] = port;
            run.Result["serverName"] = serverName;
            run.Result["saveName"] = latestSave.Name;
            run.Result["saveGameMode"] = latestSave.GameMode;
            run.Result["saveTimestamp"] = latestSave.Timestamp.ToString("O");
            run.Result["action"] = "launch-latest-save";
        }
        runSelected.Invoke(launcher, null);
        yield return null;
    }

    private static IEnumerator ConnectClient(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance ?? throw new InvalidOperationException("network-lifecycle-unavailable");
        if (lifecycle.IsServerRunning || lifecycle.IsClientRunning) throw new InvalidOperationException("network-already-running");
        string address = OptionalString(command, "address", "127.0.0.1");
        int port = RequiredInt(command, "port");
        string password = OptionalString(command, "password", string.Empty);
        string disconnectReason = string.Empty;
        lifecycle.StartClient(address, port, password, false, (reason, detail) => disconnectReason = reason + ":" + detail);
        lock (run) { run.Result["address"] = address; run.Result["port"] = port; run.Result["connectionStarted"] = true; }
        yield return null;
        if (!string.IsNullOrEmpty(disconnectReason)) throw new InvalidOperationException("connection-failed:" + disconnectReason);
    }

    private static void AddRuntimeContext(RuntimeTestRunDto run)
    {
        DebugSessionInfo session = DebugRuntime.Session;
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        run.Result["runtime"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["role"] = session?.Role ?? CurrentRole(),
            ["playerId"] = session?.PlayerId ?? LocalPlayerId(),
            ["playerName"] = session?.PlayerName ?? string.Empty,
            ["sessionId"] = session?.SessionId ?? string.Empty,
            ["processId"] = session?.ProcessId ?? 0,
            ["buildNumber"] = session?.BuildNumber ?? Multiplayer.BuildNumber,
            ["serverRunning"] = lifecycle?.IsServerRunning == true,
            ["clientRunning"] = lifecycle?.IsClientRunning == true,
            ["activeScene"] = SceneManager.GetActiveScene().name
        };
    }

    private static IEnumerator Teleport(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        if (PlayerManager.PlayerTransform == null || WorldMover.Instance == null)
            throw new InvalidOperationException("player-runtime-unavailable");
        Vector3 absolute = new(RequiredFloat(command, "x"), RequiredFloat(command, "y"), RequiredFloat(command, "z"));
        float yaw = OptionalFloat(command, "yaw", PlayerManager.PlayerTransform.eulerAngles.y);
        float tolerance = Mathf.Clamp(OptionalFloat(command, "tolerance", 0.5f), 0.01f, 10f);
        Vector3 before = PlayerManager.PlayerTransform.position - WorldMover.currentMove;
        PlayerManager.TeleportPlayer(absolute + WorldMover.currentMove, Quaternion.Euler(0f, yaw, 0f), null, true, false);
        yield return null;
        yield return new WaitForEndOfFrame();
        Vector3 after = PlayerManager.PlayerTransform.position - WorldMover.currentMove;
        float error = Vector3.Distance(absolute, after);
        lock (run)
        {
            run.Result["beforeAbsolute"] = DebugValueSnapshotter.Snapshot(before);
            run.Result["requestedAbsolute"] = DebugValueSnapshotter.Snapshot(absolute);
            run.Result["actualAbsolute"] = DebugValueSnapshotter.Snapshot(after);
            run.Result["positionError"] = error;
            run.Result["tolerance"] = tolerance;
        }
        if (error > tolerance) throw new InvalidOperationException($"teleport-position-error:{error:0.###}");
    }

    private void Finish(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        cancelled.TryRemove(command.RequestId, out _);
        DebugRuntime.Publish("runtime-test", "runtime-test.completed", DebugRuntimeSide.Shared,
            run.Status == RuntimeTestCommandStatus.Passed ? DebugSeverity.Info : DebugSeverity.Error,
            correlationId: command.RunId, data: new()
            {
                ["requestId"] = command.RequestId, ["command"] = command.Command,
                ["status"] = run.Status.ToString(), ["error"] = run.Error
            });
        RuntimeTestScope.Clear();
        active = null;
    }

    private void RefreshCapabilities()
    {
        nextCapabilityRefresh = Time.unscaledTime + 2f;
        List<string> capabilities = new() { "main-thread-command-queue", "runtime-self-check", "environment-orchestration" };
        if (PlayerManager.PlayerTransform != null && WorldMover.Instance != null) capabilities.Add("player-teleport");
        if (VRManager.IsVREnabled()) capabilities.Add("vr-runtime"); else capabilities.Add("non-vr-runtime");
        if (itemDriver.TryResolve(out _)) capabilities.Add("non-vr-item-interaction");
        if (DV.InventorySystem.Inventory.Instance != null)
            capabilities.Add("inventory-runtime-arrangement");
        if (NetworkLifecycle.Instance?.IsServerRunning == true)
            capabilities.Add("host-inventory-fixtures");
        if (!VRManager.IsVREnabled() && DV.InventorySystem.Inventory.Instance != null &&
            NetworkLifecycle.Instance?.IsClientRunning == true)
            capabilities.Add("cold-container-quick-move-scenario");
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle?.IsServerRunning == true) capabilities.Add("server-runtime");
        if (lifecycle?.IsClientRunning == true) capabilities.Add("client-runtime");
        if (lifecycle?.IsClientRunning == true && Inventory.Instance != null)
            capabilities.Add("lost-and-found-runtime-scenario");

        Dictionary<string, object> anchors = new(StringComparer.Ordinal);
        if (sceneAnchorsDirty)
        {
            sceneAnchorsDirty = false;
            cachedJobValidator = FindObjectsOfType<JobValidator>()
                .Where(item => item != null)
                .OrderBy(item => PlayerManager.PlayerTransform == null ? 0f :
                    (item.transform.position - PlayerManager.PlayerTransform.position).sqrMagnitude).FirstOrDefault();
        }
        JobValidator validator = cachedJobValidator;
        if (validator != null)
        {
            capabilities.Add("job-validator");
            ItemUseTarget target = validator.GetComponent<ItemUseTarget>();
            Transform spawn = validator.bookletPrinter?.spawnAnchor;
            anchors["jobValidator"] = new Dictionary<string, object>
            {
                ["path"] = GameObjectPath(validator.transform),
                ["absolutePosition"] = DebugValueSnapshotter.Snapshot(validator.transform.position - WorldMover.currentMove),
                ["itemUseTarget"] = target != null,
                ["targetColliderCount"] = target?.targetColliders?.Length ?? 0,
                ["bookletSpawnAnchor"] = spawn == null ? null : DebugValueSnapshotter.Snapshot(spawn.position - WorldMover.currentMove)
            };
        }
        cachedCapabilities = new RuntimeTestCapabilitiesDto
        {
            Available = true, MainThreadAgentReady = true, Role = CurrentRole(), PlayerId = LocalPlayerId(),
            Scene = SceneManager.GetActiveScene().name, Capabilities = capabilities.ToArray(),
            Commands = RuntimeTestCatalog.Commands, Tests = RuntimeTestCatalog.Descriptors, Anchors = anchors
        };
    }

    private static string Validate(RuntimeTestCommandDto command)
    {
        if (command == null) return "missing-command-body";
        if (command.SchemaVersion != RuntimeTestCapabilitiesDto.CurrentSchemaVersion) return "unsupported-schema-version";
        if (string.IsNullOrWhiteSpace(command.Command)) return "missing-command";
        RuntimeTestDescriptorDto descriptor = RuntimeTestCatalog.Find(command.Command);
        if (descriptor == null) return "unsupported-command";
        if (command.MutationKind == RuntimeTestMutationKind.Destructive) return "destructive-commands-disabled";
        if (command.MutationKind != descriptor.MutationKind) return "invalid-mutation-kind";
        return string.Empty;
    }

    private static RuntimeTestCommandAcceptedDto Rejected(RuntimeTestCommandDto command, string reason) => new()
    {
        RequestId = command?.RequestId ?? string.Empty, RunId = command?.RunId ?? string.Empty,
        Accepted = false, Reason = reason
    };

    private static RuntimeTestRunDto NewRun(RuntimeTestCommandDto command) => new()
    {
        RequestId = command.RequestId, RunId = command.RunId, CaseId = command.CaseId,
        PhaseId = command.PhaseId, StepId = command.StepId, Command = command.Command,
        Status = RuntimeTestCommandStatus.Queued, QueuedUtc = DateTime.UtcNow
    };

    private static RuntimeTestRunDto Clone(RuntimeTestRunDto source)
    {
        // Result contains live phase/assertion/resource collections. A shallow copy lets the
        // HTTP worker enumerate those collections while the main-thread coroutine mutates
        // them, intermittently returning HTTP 500 from status polling. Serialize the complete
        // DTO while holding its mutation gate so every response is an immutable point-in-time
        // snapshot.
        lock (source)
            return JsonConvert.DeserializeObject<RuntimeTestRunDto>(
                DebugJson.Serialize(source), DebugJson.Settings);
    }

    private static RuntimeTestRunSummaryDto Summarize(RuntimeTestRunDto source)
    {
        lock (source) return new RuntimeTestRunSummaryDto
        {
            RequestId = source.RequestId, RunId = source.RunId, CaseId = source.CaseId,
            Command = source.Command, Status = source.Status, QueuedUtc = source.QueuedUtc,
            StartedUtc = source.StartedUtc, CompletedUtc = source.CompletedUtc,
            PhaseId = source.PhaseId, StepId = source.StepId, Error = source.Error
        };
    }

    private static void Complete(RuntimeTestRunDto run, RuntimeTestCommandStatus status, string error)
    {
        lock (run) { run.Status = status; run.Error = error ?? string.Empty; run.CompletedUtc = DateTime.UtcNow; }
    }

    private void PruneCompletedRuns()
    {
        foreach (KeyValuePair<string, RuntimeTestRunDto> pair in runs.OrderBy(item => item.Value.QueuedUtc).Take(128).ToArray())
            if (IsTerminal(pair.Value.Status)) runs.TryRemove(pair.Key, out _);
    }

    private static bool IsTerminal(RuntimeTestCommandStatus status) => status is RuntimeTestCommandStatus.Passed
        or RuntimeTestCommandStatus.Failed or RuntimeTestCommandStatus.FailedDirty or
        RuntimeTestCommandStatus.Cancelled or RuntimeTestCommandStatus.Unsupported;
    private static bool CleanupDirty(RuntimeTestRunDto run)
    {
        lock (run) return run.Result.TryGetValue("cleanupClean", out object clean) &&
            clean is bool value && !value;
    }
    private static float RequiredFloat(RuntimeTestCommandDto command, string key) =>
        command.Parameters.TryGetValue(key, out string value) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
            ? result : throw new ArgumentException("missing-or-invalid-parameter:" + key);
    private static float OptionalFloat(RuntimeTestCommandDto command, string key, float fallback) =>
            command.Parameters.TryGetValue(key, out string value) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ? result : fallback;
    private static int RequiredInt(RuntimeTestCommandDto command, string key) =>
        command.Parameters.TryGetValue(key, out string value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result : throw new ArgumentException("missing-or-invalid-parameter:" + key);
    private static int OptionalInt(RuntimeTestCommandDto command, string key, int fallback) =>
        command.Parameters.TryGetValue(key, out string value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : fallback;
    private static string OptionalString(RuntimeTestCommandDto command, string key, string fallback) =>
        command.Parameters.TryGetValue(key, out string value) ? value : fallback;
    private static string CurrentRole()
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle?.IsServerRunning == true && lifecycle.IsClientRunning) return "host";
        if (lifecycle?.IsServerRunning == true) return "server";
        if (lifecycle?.IsClientRunning == true) return "client";
        return "standalone";
    }
    private static byte? LocalPlayerId() => NetworkLifecycle.Instance?.Client?.PlayerId;
    private static string GameObjectPath(Transform transform)
    {
        List<string> parts = new();
        while (transform != null) { parts.Add(transform.name); transform = transform.parent; }
        parts.Reverse(); return string.Join("/", parts);
    }
    private static RuntimeTestCapabilitiesDto UnavailableCapabilities() => new() { Available = false, MainThreadAgentReady = false };
    private static RuntimeTestDescriptorDto CloneDescriptor(RuntimeTestDescriptorDto value) =>
        RuntimeTestDescriptorCloner.Clone(value);

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (Instance == this) Instance = null;
        RuntimeTestScope.Clear();
    }

    private void OnSceneLoaded(Scene _, LoadSceneMode __)
    {
        sceneAnchorsDirty = true;
        cachedJobValidator = null;
        itemDriver.Invalidate();
        nextCapabilityRefresh = 0f;
    }
}
#endif
