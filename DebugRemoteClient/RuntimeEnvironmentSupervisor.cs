#if DEBUG
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplayer.DebugClient;

internal sealed class RuntimeEnvironmentSupervisor : IDisposable
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetWindowText(IntPtr window, string text);
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    private const int ShowMinimizedNoActivate = 7;
    private const string NoCursorCaptureArgument = "--dvmp-no-cursor-capture";

    private readonly Func<IEnumerable<DebugSessionInfo>> sessions;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource operation;
    private Process host;
    private Process client;
    private RuntimeEnvironmentStatusDto status = NewStatus(RuntimeEnvironmentStage.Idle, "Environment is idle.");

    public RuntimeEnvironmentSupervisor(Func<IEnumerable<DebugSessionInfo>> sessions)
    {
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _ = Task.Run(() => AdoptExistingEnvironment(lifetime.Token));
    }

    public RuntimeEnvironmentStatusDto GetStatus() { lock (gate) return Clone(status); }

    public RuntimeEnvironmentStatusDto Start(RuntimeEnvironmentStartRequestDto request)
    {
        request ??= new RuntimeEnvironmentStartRequestDto();
        lock (gate)
        {
            if (status.Active) return Clone(status);
            try { ValidateLaunchConfiguration(request); }
            catch (Exception exception)
            {
                status = NewStatus(RuntimeEnvironmentStage.Failed, "Environment configuration is invalid.");
                status.Error = exception.GetBaseException().Message;
                return Clone(status);
            }
            operation?.Dispose();
            operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            if (!HasExactManualBaseline(request))
            {
                status = NewStatus(RuntimeEnvironmentStage.DiscoveringBaselineSaves,
                    "No exact manual baseline is configured. Discovering available manual saves.");
                _ = Task.Run(() => DiscoverBaselineSaves(request, operation.Token));
                return Clone(status);
            }
            status = NewStatus(RuntimeEnvironmentStage.LaunchingHost, "Starting host process.");
            _ = Task.Run(() => Run(request, operation.Token));
            return Clone(status);
        }
    }

    public RuntimeEnvironmentStatusDto Stop()
    {
        lock (gate) Set(RuntimeEnvironmentStage.Stopping, "Stopping managed game processes.");
        operation?.Cancel();
        TryStop(client);
        TryStop(host);
        lock (gate)
        {
            client = null; host = null;
            status = NewStatus(RuntimeEnvironmentStage.Stopped, "Managed game processes stopped.");
            return Clone(status);
        }
    }

    private async Task Run(RuntimeEnvironmentStartRequestDto request, CancellationToken token)
    {
        try
        {
            host = Launch(request, request.HostArguments);
            SetProcess(true, host.Id);
            _ = ApplyManagedWindow(host, "Derail Valley — DVMP HOST",
                request.MinimizeManagedWindows, token);

            // Unity Mod Manager uses shared on-disk cache state. Let the host finish that
            // narrow phase before starting the second process, then overlap game/scene boot.
            Set(RuntimeEnvironmentStage.WaitingForHostAgent, "Waiting for the host mod loader and debug agent before starting the client.");
            DebugSessionInfo hostSession = await WaitForSession(host.Id, request.AgentTimeoutSeconds, token).ConfigureAwait(false);
            SetSession(true, hostSession.SessionId);

            client = Launch(request, request.ClientArguments);
            SetProcess(false, client.Id);
            _ = ApplyManagedWindow(client, "Derail Valley — DVMP CLIENT",
                request.MinimizeManagedWindows, token);
            Task<DebugSessionInfo> clientAgent = WaitForSession(client.Id, request.AgentTimeoutSeconds, token);
            Task<DebugSessionInfo> clientMenuReady = WaitForClientMenu(clientAgent, request.AgentTimeoutSeconds, token);

            await WaitUntil(hostSession, request.AgentTimeoutSeconds, token, result => Bool(result, "mainMenuReady"), true).ConfigureAwait(false);

            Set(RuntimeEnvironmentStage.LoadingHostSave,
                "Loading the configured exact manual baseline save through Derail Valley's Continue action.");
            RuntimeTestRunDto saveRun = await Command(hostSession, "environment.host-save", RuntimeTestMutationKind.IsolatedMutation,
                request.WorldTimeoutSeconds * 1000, new()
                {
                    ["port"] = request.Port.ToString(CultureInfo.InvariantCulture), ["password"] = request.Password ?? string.Empty,
                    ["serverName"] = request.ServerName ?? string.Empty, ["maxPlayers"] = request.MaxPlayers.ToString(CultureInfo.InvariantCulture),
                    ["saveUid"] = request.HostSaveUid?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, ["saveName"] = request.HostSaveName ?? string.Empty,
                    ["saveGameMode"] = request.HostSaveGameMode ?? string.Empty,
                    ["saveBasePath"] = request.HostSaveBasePath ?? string.Empty
                }, token).ConfigureAwait(false);
            lock (gate) { status.HostSaveSelection = new(saveRun.Result ?? new()); status.UpdatedUtc = DateTime.UtcNow; }

            Set(RuntimeEnvironmentStage.WaitingForHostServer, "Waiting for the save, player, and host server to become ready.");
            await WaitUntil(hostSession, request.WorldTimeoutSeconds, token,
                result => Bool(result, "serverRunning") && Bool(result, "clientRunning") && Bool(result, "playerReady") && Bool(result, "itemsLoaded"), true).ConfigureAwait(false);

            Set(RuntimeEnvironmentStage.WaitingForClientAgent, "Host server is ready; waiting for the already-running client menu.");
            DebugSessionInfo clientSession = await clientMenuReady.ConfigureAwait(false);
            SetSession(false, clientSession.SessionId);

            Set(RuntimeEnvironmentStage.ConnectingClient, "Connecting the client to the ready host.");
            await Command(clientSession, "environment.connect-client", RuntimeTestMutationKind.IsolatedMutation,
                15000, new()
                {
                    ["address"] = request.Address ?? "127.0.0.1", ["port"] = request.Port.ToString(CultureInfo.InvariantCulture),
                    ["password"] = request.Password ?? string.Empty
                }, token).ConfigureAwait(false);

            Set(RuntimeEnvironmentStage.WaitingForClientWorld, "Waiting for client world synchronization to complete.");
            await WaitUntil(clientSession, request.WorldTimeoutSeconds, token,
                result => Bool(result, "clientRunning") && Bool(result, "itemsLoaded") && Bool(result, "networkComplete"), false).ConfigureAwait(false);
            Set(RuntimeEnvironmentStage.Ready, "Host and client are loaded and ready for runtime tests.");
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) Set(RuntimeEnvironmentStage.Stopped, "Environment startup cancelled."); }
        catch (Exception exception)
        {
            lock (gate)
            {
                status.Stage = RuntimeEnvironmentStage.Failed; status.Active = false; status.Ready = false;
                status.Error = exception.GetBaseException().Message; status.Message = "Environment startup failed."; status.UpdatedUtc = DateTime.UtcNow;
            }
        }
    }

    private async Task DiscoverBaselineSaves(RuntimeEnvironmentStartRequestDto request,
        CancellationToken token)
    {
        try
        {
            host = Launch(request, request.HostArguments);
            SetProcess(true, host.Id);
            _ = ApplyManagedWindow(host, "Derail Valley â€” DVMP SAVE DISCOVERY",
                request.MinimizeManagedWindows, token);

            Set(RuntimeEnvironmentStage.WaitingForHostAgent,
                "Waiting for the host debug agent so it can list manual saves.");
            DebugSessionInfo hostSession = await WaitForSession(host.Id, request.AgentTimeoutSeconds,
                token).ConfigureAwait(false);
            SetSession(true, hostSession.SessionId);
            await WaitUntil(hostSession, request.AgentTimeoutSeconds, token,
                result => Bool(result, "mainMenuReady"), true).ConfigureAwait(false);

            RuntimeTestRunDto catalog = await Command(hostSession, "environment.save-catalog",
                RuntimeTestMutationKind.ReadOnly, 5000, new(), token).ConfigureAwait(false);
            Dictionary<string, object> saves = new(catalog.Result ?? new(),
                StringComparer.Ordinal);

            TryStop(host);
            host = null;
            lock (gate)
            {
                status = NewStatus(RuntimeEnvironmentStage.BaselineRequired,
                    "Select one manual save from the catalogue, then configure its UID, name, game mode, and save path before launching tests.");
                status.AvailableSaves = saves;
                status.UpdatedUtc = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested)
            {
                TryStop(host);
                host = null;
                Set(RuntimeEnvironmentStage.Stopped, "Baseline-save discovery cancelled.");
            }
        }
        catch (Exception exception)
        {
            TryStop(host);
            host = null;
            lock (gate)
            {
                status = NewStatus(RuntimeEnvironmentStage.BaselineRequired,
                    "An exact manual baseline is required before tests can launch.");
                status.Error = "manual-save-catalog-failed:" + exception.GetBaseException().Message;
                status.UpdatedUtc = DateTime.UtcNow;
            }
        }
    }

    private Process Launch(RuntimeEnvironmentStartRequestDto request, string roleArguments)
    {
        string working = string.IsNullOrWhiteSpace(request.WorkingDirectory) ? Path.GetDirectoryName(request.ExecutablePath) : request.WorkingDirectory;
        Process process = Process.Start(new ProcessStartInfo
        {
            FileName = request.ExecutablePath, WorkingDirectory = working,
            Arguments = JoinArguments(request.CommonArguments, roleArguments,
                request.DisableManagedWindowInput ? NoCursorCaptureArgument : string.Empty), UseShellExecute = true,
            WindowStyle = request.MinimizeManagedWindows
                ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
        });
        return process ?? throw new InvalidOperationException("game-process-did-not-start");
    }

    private static async Task ApplyManagedWindow(Process process, string title, bool minimize,
        CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            try
            {
                if (process.HasExited) return;
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    SetWindowText(process.MainWindowHandle, title);
                    if (minimize) ShowWindowAsync(process.MainWindowHandle, ShowMinimizedNoActivate);
                    return;
                }
            }
            catch { return; }
            await Task.Delay(process.MainWindowHandle == IntPtr.Zero ? 50 : 1000, token)
                .ConfigureAwait(false);
        }
    }

    private async Task<DebugSessionInfo> WaitForSession(int processId, int timeoutSeconds, CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            DebugSessionInfo match = (sessions() ?? Array.Empty<DebugSessionInfo>()).FirstOrDefault(item => item?.ProcessId == processId);
            if (match != null && !string.IsNullOrWhiteSpace(match.FirehoseUrl)) return match;
            await Task.Delay(500, token).ConfigureAwait(false);
        }
        throw new TimeoutException("debug-agent-timeout:pid=" + processId);
    }

    private async Task AdoptExistingEnvironment(CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            DebugSessionInfo[] live = (sessions() ?? Array.Empty<DebugSessionInfo>()).Where(value => value != null).ToArray();
            DebugSessionInfo existingHost = live.Where(value => string.Equals(value.Role, "host", StringComparison.OrdinalIgnoreCase)).OrderByDescending(value => value.HeartbeatUtc).FirstOrDefault();
            DebugSessionInfo existingClient = live.Where(value => string.Equals(value.Role, "client", StringComparison.OrdinalIgnoreCase)).OrderByDescending(value => value.HeartbeatUtc).FirstOrDefault();
            if (existingHost != null && existingClient != null)
            {
                try
                {
                    RuntimeTestRunDto hostRun = await Command(existingHost, "environment.status", RuntimeTestMutationKind.ReadOnly, 5000, new(), token).ConfigureAwait(false);
                    RuntimeTestRunDto clientRun = await Command(existingClient, "environment.status", RuntimeTestMutationKind.ReadOnly, 5000, new(), token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (status.Stage != RuntimeEnvironmentStage.Idle) return;
                        status = NewStatus(RuntimeEnvironmentStage.Ready, "Attached to an existing host and client environment.");
                        status.HostProcessId = existingHost.ProcessId;
                        status.ClientProcessId = existingClient.ProcessId;
                        status.HostSessionId = existingHost.SessionId;
                        status.ClientSessionId = existingClient.SessionId;
                        status.HostReadiness = new(hostRun.Result ?? new());
                        status.ClientReadiness = new(clientRun.Result ?? new());
                    }
                    return;
                }
                catch { }
            }
            try { await Task.Delay(500, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<DebugSessionInfo> WaitForClientMenu(Task<DebugSessionInfo> agent, int timeoutSeconds, CancellationToken token)
    {
        DebugSessionInfo session = await agent.ConfigureAwait(false);
        SetSession(false, session.SessionId);
        await WaitUntil(session, timeoutSeconds, token, result => Bool(result, "mainMenuReady"), false).ConfigureAwait(false);
        return session;
    }

    private async Task WaitUntil(DebugSessionInfo session, int timeoutSeconds, CancellationToken token,
        Func<Dictionary<string, object>, bool> predicate, bool isHost)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, timeoutSeconds));
        string lastError = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                RuntimeTestRunDto run = await Command(session, "environment.status", RuntimeTestMutationKind.ReadOnly, 5000, new(), token).ConfigureAwait(false);
                Dictionary<string, object> result = run.Result ?? new();
                lock (gate) { if (isHost) status.HostReadiness = new(result); else status.ClientReadiness = new(result); status.UpdatedUtc = DateTime.UtcNow; }
                if (predicate(result)) return;
            }
            catch (Exception exception) { lastError = exception.GetBaseException().Message; }
            await Task.Delay(1000, token).ConfigureAwait(false);
        }
        throw new TimeoutException("runtime-readiness-timeout:" + lastError);
    }

    private static async Task<RuntimeTestRunDto> Command(DebugSessionInfo session, string command,
        RuntimeTestMutationKind mutation, int timeoutMilliseconds, Dictionary<string, string> parameters, CancellationToken token)
    {
        RuntimeTestHttpClient api = new(session);
        string requestId = Guid.NewGuid().ToString("N");
        RuntimeTestCommandAcceptedDto accepted = api.Enqueue(new RuntimeTestCommandDto
        {
            RequestId = requestId, RunId = "environment-" + Guid.NewGuid().ToString("N"), Command = command,
            MutationKind = mutation, TimeoutMilliseconds = timeoutMilliseconds, Parameters = parameters
        });
        if (accepted?.Accepted != true) throw new InvalidOperationException("environment-command-rejected:" + accepted?.Reason);
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds + 5000);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            RuntimeTestRunDto run = api.GetRun(requestId);
            if (run?.Status == RuntimeTestCommandStatus.Passed) return run;
            if (run?.Status is RuntimeTestCommandStatus.Failed or RuntimeTestCommandStatus.FailedDirty or RuntimeTestCommandStatus.Cancelled or RuntimeTestCommandStatus.Unsupported)
                throw new InvalidOperationException(command + ":" + run.Error);
            await Task.Delay(250, token).ConfigureAwait(false);
        }
        throw new TimeoutException("environment-command-timeout:" + command);
    }

    private static bool Bool(Dictionary<string, object> result, string key) =>
        result.TryGetValue(key, out object value) && (value is bool flag ? flag : bool.TryParse(value?.ToString(), out bool parsed) && parsed);
    private static string JoinArguments(params string[] values) => string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static void ValidateLaunchConfiguration(RuntimeEnvironmentStartRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.ExecutablePath) || !File.Exists(request.ExecutablePath)) throw new FileNotFoundException("Derail Valley executable not found.", request.ExecutablePath);
        if (request.Port < 1024 || request.Port > 49151) throw new ArgumentOutOfRangeException(nameof(request.Port));
    }
    private static bool HasExactManualBaseline(RuntimeEnvironmentStartRequestDto request) =>
        request.HostSaveUid.HasValue && !string.IsNullOrWhiteSpace(request.HostSaveName) &&
        !string.IsNullOrWhiteSpace(request.HostSaveGameMode) &&
        !string.IsNullOrWhiteSpace(request.HostSaveBasePath);
    private void Set(RuntimeEnvironmentStage stage, string message) { lock (gate) { status.Stage = stage; status.Message = message; status.Error = string.Empty; status.Active = IsActive(stage); status.Ready = stage == RuntimeEnvironmentStage.Ready; status.UpdatedUtc = DateTime.UtcNow; } }
    private void SetProcess(bool isHost, int id) { lock (gate) { if (isHost) status.HostProcessId = id; else status.ClientProcessId = id; status.UpdatedUtc = DateTime.UtcNow; } }
    private void SetSession(bool isHost, string id) { lock (gate) { if (isHost) status.HostSessionId = id; else status.ClientSessionId = id; status.UpdatedUtc = DateTime.UtcNow; } }
    private static void TryStop(Process process) { try { if (process != null && !process.HasExited) process.Kill(); } catch { } }
    private static bool IsActive(RuntimeEnvironmentStage stage) => stage is not RuntimeEnvironmentStage.Idle and
        not RuntimeEnvironmentStage.BaselineRequired and not RuntimeEnvironmentStage.Stopped and
        not RuntimeEnvironmentStage.Failed;
    private static RuntimeEnvironmentStatusDto NewStatus(RuntimeEnvironmentStage stage, string message) => new() { Stage = stage, Message = message, Active = IsActive(stage), Ready = stage == RuntimeEnvironmentStage.Ready, UpdatedUtc = DateTime.UtcNow };
    private static RuntimeEnvironmentStatusDto Clone(RuntimeEnvironmentStatusDto value) => new() { Stage = value.Stage, Active = value.Active, Ready = value.Ready, Message = value.Message, Error = value.Error, UpdatedUtc = value.UpdatedUtc, HostProcessId = value.HostProcessId, ClientProcessId = value.ClientProcessId, HostSessionId = value.HostSessionId, ClientSessionId = value.ClientSessionId, AvailableSaves = new(value.AvailableSaves), HostSaveSelection = new(value.HostSaveSelection), HostReadiness = new(value.HostReadiness), ClientReadiness = new(value.ClientReadiness) };
    public void Dispose()
    {
        lifetime.Cancel();
        operation?.Cancel();
        operation?.Dispose();
        lifetime.Dispose();
    }
}
#endif
