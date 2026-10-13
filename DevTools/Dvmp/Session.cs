using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Dvmp;

/// <param name="Background">Start minimised, below-normal priority, so the games stay out of the way.</param>
/// <param name="FpsCap">Frame cap while focused (the agent uses a lower one in the background).</param>
internal sealed record UpOptions(bool Fresh, string? Save, int Width, int Height, bool Arrange,
    bool Background = true, int FpsCap = 30, bool Sound = false);

/// <summary>
/// Brings up (or reuses) a host + client pair of real game instances joined to each other.
/// Runs as a single background operation so MCP calls can return early and resume.
/// </summary>
internal static class Session
{
    private const string ClientName = "DVMP-Client";
    private const string ClientGuid = "00000000-0000-0000-0000-00000000c11e";

    private static readonly object gate = new();
    private static Task<JsonObject>? running;
    private static string stage = "idle";
    private static readonly List<JsonObject> timeline = new();
    private static Stopwatch clock = new();

    public static string Stage { get { lock (gate) return stage; } }

    /// <summary>Starts an up operation unless one is already running, then waits up to <paramref name="wait"/>.</summary>
    public static async Task<JsonObject> UpAsync(UpOptions options, TimeSpan wait, CancellationToken ct)
    {
        Task<JsonObject> task;
        lock (gate)
        {
            if (running == null || running.IsCompleted)
            {
                timeline.Clear();
                clock = Stopwatch.StartNew();
                running = Task.Run(() => RunUpAsync(options));
            }
            task = running;
        }

        Task finished = await Task.WhenAny(task, Task.Delay(wait, ct));
        if (finished == task) return await task;
        return Progress(done: false);
    }

    private static JsonObject Progress(bool done)
    {
        lock (gate)
        {
            return new JsonObject
            {
                ["done"] = done,
                ["stage"] = stage,
                ["elapsedSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
                ["timeline"] = new JsonArray(timeline.Select(t => (JsonNode)t.DeepClone()).ToArray()),
                ["hint"] = done ? null : "still starting; call dvmp_up again to keep waiting"
            };
        }
    }

    private static void Mark(string next)
    {
        lock (gate)
        {
            stage = next;
            timeline.Add(new JsonObject { ["t"] = Math.Round(clock.Elapsed.TotalSeconds, 1), ["stage"] = next });
        }
    }

    private static async Task<JsonObject> RunUpAsync(UpOptions o)
    {
        try
        {
            Mark("checking existing games");
            List<Agent> agents = await Agents.DiscoverAsync();
            Agent? host = agents.FirstOrDefault(a => a.Role == "host" && WorldLoaded(a));
            Agent? client = agents.FirstOrDefault(a => a.Role == "client" && WorldLoaded(a));
            if (!o.Fresh && host != null && client != null)
            {
                Mark("reusing running session");
                return Result(host, client, reused: true);
            }

            // A healthy host with no client (e.g. the client quit or crashed): keep the host's
            // world as it is and only bring a client back. This is also how rejoining is tested.
            if (!o.Fresh && host != null && agents.Count == 1)
            {
                Mark("host healthy; launching client only");
                JsonObject server = await RunProbeExpressionAsync(host,
                    "new { port = global::Multiplayer.Multiplayer.Settings.Port, password = global::Multiplayer.Multiplayer.Settings.Password ?? \"\" }");
                Process lone = Launch("client", o);
                Agent rejoined = await JoinAsync(lone, server["port"]!.GetValue<int>(), server["password"]!.GetValue<string>(), o);
                Mark("ready");
                Win32.SetTitle(rejoined.Pid, "DVMP CLIENT (" + ClientName + ")");
                return Result(host, rejoined, reused: false);
            }

            if (agents.Count > 0)
            {
                Mark($"stopping {agents.Count} existing game(s)");
                await DownAsync(CancellationToken.None);
            }

            Mark("launching host");
            Process hostProcess = Launch("host", o);
            Agent hostAgent = await WaitForAgentAsync(hostProcess, TimeSpan.FromSeconds(90));
            // Unity Mod Manager touches shared cache files while loading; start the client only
            // once the host's mods are loaded.
            Mark("launching client");
            Process clientProcess = Launch("client", o);

            Mark("host: waiting for main menu");
            await WaitForMenuAsync(hostAgent, TimeSpan.FromSeconds(120));
            Mark("host: loading save");
            Dictionary<string, string> hostArgs = new();
            if (!string.IsNullOrWhiteSpace(o.Save)) hostArgs["save"] = o.Save!;
            JsonObject hosted = await RunProbeAsync(hostAgent, "host", hostArgs);
            int port = hosted["port"]?.GetValue<int>() ?? 7777;
            string password = hosted["password"]?.GetValue<string>() ?? string.Empty;

            if (o.Arrange && !o.Background) Arrange(hostProcess, clientProcess, o);

            Mark("host: waiting for world + server");
            hostAgent = await WaitUntilAsync(hostAgent.Pid, a => a.Role == "host" && WorldLoaded(a), TimeSpan.FromSeconds(300), "host world load");

            Agent clientAgent = await JoinAsync(clientProcess, port, password, o);

            Mark("ready");
            Win32.SetTitle(hostAgent.Pid, "DVMP HOST");
            Win32.SetTitle(clientAgent.Pid, "DVMP CLIENT (" + ClientName + ")");
            JsonObject result = Result(hostAgent, clientAgent, reused: false);
            result["save"] = hosted["save"]?.DeepClone();
            return result;
        }
        catch (Exception e)
        {
            Mark("failed: " + e.Message);
            JsonObject failed = Progress(done: true);
            failed["ok"] = false;
            failed["error"] = e.Message;
            return failed;
        }
    }

    // Waits for a launched client process to reach the menu, joins it to the host, and waits
    // until its world has loaded.
    private static async Task<Agent> JoinAsync(Process clientProcess, int port, string password, UpOptions o)
    {
        Agent clientAgent = await WaitForAgentAsync(clientProcess, TimeSpan.FromSeconds(90));
        Mark("client: waiting for main menu");
        await WaitForMenuAsync(clientAgent, TimeSpan.FromSeconds(120));
        Mark("client: joining");
        await RunProbeAsync(clientAgent, "join", new()
        {
            ["address"] = "127.0.0.1",
            ["port"] = port.ToString(),
            ["password"] = password,
            ["name"] = ClientName,
            ["guid"] = ClientGuid
        });
        Mark("client: waiting for world");
        return await WaitUntilAsync(clientAgent.Pid, a => a.Role == "client" && WorldLoaded(a), TimeSpan.FromSeconds(300),
            "client world load", failFast: ClientDisconnectAsync);
    }

    private static async Task<JsonObject> RunProbeExpressionAsync(Agent agent, string expression)
    {
        JsonObject r = await Operations.EvalAsync(expression, agent.Pid.ToString(), "expression", null, null, 15);
        JsonNode? single = r["results"]?.AsObject().FirstOrDefault().Value;
        if (r["compileError"] is { } compile) throw new Exception("probe failed to compile: " + compile);
        if (single?["ok"]?.GetValue<bool>() != true) throw new Exception($"probe failed on {agent.Label}: {single?["error"]}");
        return single["result"] as JsonObject ?? new JsonObject();
    }

    private static JsonObject Result(Agent host, Agent client, bool reused)
    {
        JsonObject result = Progress(done: true);
        result["ok"] = true;
        result["reused"] = reused;
        result["host"] = new JsonObject { ["pid"] = host.Pid, ["playerAbs"] = host.Status?["playerAbs"]?.DeepClone() };
        result["client"] = new JsonObject { ["pid"] = client.Pid, ["playerAbs"] = client.Status?["playerAbs"]?.DeepClone() };
        return result;
    }

    public static async Task<JsonObject> DownAsync(CancellationToken ct)
    {
        List<Agent> agents = await Agents.DiscoverAsync(ct);
        JsonArray stopped = new();
        await Task.WhenAll(agents.Select(async agent =>
        {
            try { await Agents.CallAsync(agent, "/v1/quit", new JsonObject(), TimeSpan.FromSeconds(5), ct); }
            catch (AgentException) { }
            bool exited = await WaitForExitAsync(agent.Pid, TimeSpan.FromSeconds(20));
            if (!exited)
            {
                try { Process.GetProcessById(agent.Pid).Kill(); } catch { }
            }
            lock (stopped) stopped.Add(new JsonObject { ["pid"] = agent.Pid, ["role"] = agent.Role, ["killed"] = !exited });
        }));
        return new JsonObject { ["stopped"] = stopped };
    }

    // ------------------------------------------------------------------ helpers

    private static Process Launch(string role, UpOptions o)
    {
        if (!File.Exists(Paths.GameExe)) throw new Exception($"game not found at {Paths.GameExe} (set DVMP_DV_DIR)");
        string logs = Path.Combine(Paths.DataRoot, "logs");
        Directory.CreateDirectory(logs);
        string args = string.Join(" ",
            "-nonvr",
            "-screen-fullscreen 0",
            $"-screen-width {o.Width}",
            $"-screen-height {o.Height}",
            $"-logFile \"{Path.Combine(logs, role + ".log")}\"",
            $"-dvmp-role {role}",
            $"-dvmp-fps {o.FpsCap}",
            o.Sound ? "-dvmp-sound" : string.Empty);
        Process process = Process.Start(new ProcessStartInfo(Paths.GameExe, args)
        {
            WorkingDirectory = Paths.GameDir,
            UseShellExecute = true,
            WindowStyle = o.Background ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
        }) ?? throw new Exception($"failed to start {role} process");
        // Whatever the user is doing in the foreground (e.g. VR) wins any CPU contention.
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        return process;
    }

    private static async Task<Agent> WaitForAgentAsync(Process process, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (process.HasExited) throw new Exception($"game process {process.Id} exited during startup (code {process.ExitCode})");
            Agent? agent = (await Agents.DiscoverAsync()).FirstOrDefault(a => a.Pid == process.Id && a.Status != null);
            if (agent != null) return agent;
            await Task.Delay(500);
        }
        throw new Exception($"dev agent in process {process.Id} did not start within {timeout.TotalSeconds:0}s " +
                            "(is MultiplayerDevTools enabled in Unity Mod Manager?)");
    }

    private static async Task WaitForMenuAsync(Agent agent, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            JsonObject r = await Operations.EvalAsync(
                "UnityEngine.Object.FindObjectOfType<DV.UI.MainMenuController>()?.rightPaneController?.launcherController != null",
                agent.Pid.ToString(), "expression", null, null, 10);
            if (r["results"]?.AsObject().FirstOrDefault().Value?["result"]?.GetValue<bool>() == true) return;
            await Task.Delay(500);
        }
        throw new Exception($"{agent.Label}: main menu not ready within {timeout.TotalSeconds:0}s");
    }

    private static async Task<JsonObject> RunProbeAsync(Agent agent, string name, Dictionary<string, string> args)
    {
        JsonObject r = await Operations.EvalAsync(LoadProbe(name), agent.Pid.ToString(), "class", null, args, 30);
        if (r["compileError"] is { } compile) throw new Exception($"{name} probe failed to compile: {compile}");
        JsonNode? single = r["results"]?.AsObject().FirstOrDefault().Value;
        if (single?["ok"]?.GetValue<bool>() != true)
            throw new Exception($"{name} probe failed on {agent.Label}: {single?["error"]}");
        return single["result"] as JsonObject ?? new JsonObject();
    }

    private static async Task<Agent> WaitUntilAsync(int pid, Func<Agent, bool> condition, TimeSpan timeout, string what,
        Func<Agent, Task<string?>>? failFast = null)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            Agent? agent = (await Agents.DiscoverAsync()).FirstOrDefault(a => a.Pid == pid);
            if (agent == null && !IsAlive(pid)) throw new Exception($"{what}: process {pid} exited");
            if (agent?.Status != null)
            {
                if (condition(agent)) return agent;
                if (failFast != null && await failFast(agent) is { } reason) throw new Exception($"{what}: {reason}");
            }
            await Task.Delay(1000);
        }
        throw new Exception($"{what} did not finish within {timeout.TotalSeconds:0}s");
    }

    private static async Task<string?> ClientDisconnectAsync(Agent agent)
    {
        JsonObject r = await Operations.EvalAsync(
            "DevContext.State.TryGetValue(\"dvmp.join.disconnect\", out var v) ? v : null",
            agent.Pid.ToString(), "expression", null, null, 5);
        JsonNode? value = r["results"]?.AsObject().FirstOrDefault().Value?["result"];
        return value is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s) ? "disconnected: " + s : null;
    }

    private static bool WorldLoaded(Agent agent) => agent.Status?["worldLoaded"]?.GetValue<bool?>() == true;

    private static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!IsAlive(pid)) return true;
            await Task.Delay(250);
        }
        return !IsAlive(pid);
    }

    private static bool IsAlive(int pid)
    {
        try { using Process p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    internal static string LoadProbe(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("probe." + name)
                              ?? throw new Exception($"embedded probe '{name}' missing");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    // Put the host on the left and the client on the right of the primary screen.
    private static void Arrange(Process host, Process client, UpOptions o)
    {
        try
        {
            host.Refresh();
            client.Refresh();
            if (host.MainWindowHandle != IntPtr.Zero)
                SetWindowPos(host.MainWindowHandle, IntPtr.Zero, 0, 0, 0, 0, NoSizeNoZOrder);
            if (client.MainWindowHandle != IntPtr.Zero)
                SetWindowPos(client.MainWindowHandle, IntPtr.Zero, o.Width + 16, 0, 0, 0, NoSizeNoZOrder);
        }
        catch { }
    }

    private const uint NoSizeNoZOrder = 0x0001 | 0x0004 | 0x0010; // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
