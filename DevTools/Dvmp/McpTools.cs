using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dvmp;

[McpServerToolType]
public static class McpTools
{
    private static readonly JsonSerializerOptions pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const string Session =
        "Named session (an independent host + client pair, e.g. p1, d1; docs/design/parallel-test-sessions.md). " +
        "Default: DVMP_SESSION, else \"default\".";

    [McpServerTool(Name = "dvmp_status", ReadOnly = true, Idempotent = true)]
    [Description("""
        Lists running Derail Valley processes that have the DVMP dev agent, with their
        multiplayer role (host/client/offline), scene, whether the world is loaded, fps,
        and the local player's absolute position. Call this first. Also lists paired remote
        games (target remote:NAME; see `dvmp remote` in DevTools/README.md) without contacting them.
        `warnings` flags probe coroutines still running after their probe and Harmony patches
        probes left installed.
        """)]
    public static async Task<string> Status(
        [Description(Session)] string? session = null,
        [Description("Every session's games, not just this session's. Default false.")] bool all = false,
        CancellationToken ct = default)
    {
        using IDisposable scope = Sessions.Use(session);
        return Render(await Operations.StatusAsync(ct, all));
    }

    [McpServerTool(Name = "dvmp_up")]
    [Description("""
        Ensures a host and a client game instance are running in desktop mode and joined to
        each other over loopback, launching them if needed (reuses a healthy running pair).
        A cold start takes a minute or more: this waits up to waitSeconds and, if not done,
        returns the current stage; call it again to keep waiting on the same launch.
        The host loads the save shown in its launcher unless `save` names one. Test sessions
        are never advertised to the lobby server or Steam lobbies.
        Pairs run side by side in named sessions (`session`: p1, d1, ...), each with its own user
        data. `dedicated` makes the host a dedicated server (no player); `hostOnly` starts only a
        host. All pairs share one game install: a mod build (Debug deploys) changes them all.
        """)]
    public static async Task<string> Up(
        [Description("Restart both games even if a healthy session is running.")] bool fresh = false,
        [Description("Save name or UID for the host to load. Default: the launcher's current save.")] string? save = null,
        [Description("Seconds to wait before returning progress. Default 50.")] int waitSeconds = 50,
        [Description("Start minimised at below-normal priority (default true). Games launched by the harness are always muted and run at low graphics settings.")] bool background = true,
        [Description("Frame cap while a game window is focused. Default 30; unfocused games run at 20.")] int fpsCap = 30,
        [Description("Leave game audio on. Default false.")] bool sound = false,
        [Description("Start a brand-new session instead of a save: \"career\" or \"sandbox\" (requires fresh or no running games).")] string? newSession = null,
        [Description("Install the mutation tracer at the main menu, before the world loads, so load-time item changes are recorded (see dvmp_trace).")] bool trace = false,
        [Description("Restart both games with the session's own user data copied afresh from the real profile (named sessions only).")] bool reseed = false,
        [Description("Launch the host as a dedicated server (-mp-dedicated): no avatar, no player slot.")] bool dedicated = false,
        [Description("Start or reuse only a host, with its own identity (needs a named session).")] bool hostOnly = false,
        [Description("Launch the host checking Steam tickets (no -mp-allow-unverified); use with fresh.")] bool verified = false,
        [Description("Name for a brand-new session started with newSession.")] string? newSessionName = null,
        [Description(Session)] string? session = null,
        CancellationToken ct = default)
    {
        using IDisposable scope = Sessions.Use(session);
        return Render(await Dvmp.Session.UpAsync(new UpOptions(fresh, save, 960, 540, Arrange: true, background, Math.Clamp(fpsCap, 5, 240), sound, trace, newSession,
                NewSessionName: newSessionName, HostOnly: hostOnly, Dedicated: dedicated, Verified: verified, Reseed: reseed),
            TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 600)), ct));
    }

    [McpServerTool(Name = "dvmp_down", Destructive = true)]
    [Description("Quits this session's games (every session's with all), killing any that don't exit within 20s. Only games dvmp launched: one the user started themselves (it has the agent too) is never touched, and is listed as leftRunning.")]
    public static async Task<string> Down(
        [Description(Session)] string? session = null,
        [Description("Every session's games. Default false.")] bool all = false,
        CancellationToken ct = default)
    {
        using IDisposable scope = Sessions.Use(session);
        return Render(await Dvmp.Session.DownAsync(ct, all));
    }

    [McpServerTool(Name = "dvmp_desync", ReadOnly = true)]
    [Description("""
        Desync checker. Snapshots the actual Unity state of every networked item and car (plus
        money and junctions) on host and client `samples` times, compares them, and reports
        divergences that persist across most samples, so in-flight latency is not counted.

        Kinds: item.missing-on-client (host thinks the client has it), item.ghost-on-client,
        item.unexpected-on-client (client kept an item the host retired), item.state /
        parent / active / revision / position / kinematic, car.missing/ghost, car.coupling /
        derailed / position / track / handbrake / brake-pipe, world.money / junction.
        Positions are compared in absolute space, or car-local for items riding in a car.
        Returns counts per kind and the top persistent divergences; the full report is
        written to reportPath. Requires a running host + client (dvmp_up).
        """)]
    public static async Task<string> DesyncCheck(
        [Description("Number of snapshots. Default 6.")] int samples = 6,
        [Description("Seconds between snapshots. Default 1.")] double intervalSeconds = 1,
        [Description("How many persistent divergences to list. Default 40.")] int top = 40,
        [Description(Session)] string? session = null,
        CancellationToken ct = default)
    {
        using IDisposable scope = Sessions.Use(session);
        try { return Render(await Desync.RunAsync(Math.Clamp(samples, 1, 120), Math.Clamp(intervalSeconds, 0.1, 30), Math.Clamp(top, 1, 500), ct)); }
        catch (Exception e) when (e is AgentException or ProbeCompileException)
        {
            return Render(new JsonObject { ["ok"] = false, ["error"] = e.Message });
        }
    }

    [McpServerTool(Name = "dvmp_soak")]
    [Description("""
        Soak test: a seeded bot performs random item interactions (carry, throw, pick-and-drop,
        wander) around the player on the chosen peer(s), then dvmp_desync runs. Use target "all"
        to have host and client act at the same time (different seeds) to provoke ownership
        conflicts. Returns per-peer action counts, any failed actions, and the desync report;
        the full action log is written to soakLogPath. Roughly 6-8 s per action, so prefer the
        CLI (`dvmp soak`) in the background for long runs.
        """)]
    public static async Task<string> SoakTest(
        [Description("client (default), host, or all.")] string target = "client",
        [Description("Actions per peer. Default 15.")] int actions = 15,
        [Description("Random seed; the same seed replays the same choices. Default 1.")] int seed = 1,
        [Description("Radius in metres around the starting point to pick items from. Default 40.")] double radius = 40,
        [Description("Seconds between actions. Default 1.")] double pause = 1,
        [Description("Desync samples afterwards. Default 6.")] int desyncSamples = 6,
        [Description("Only touch items whose name starts with this, e.g. \"JobOverview\".")] string? filter = null,
        [Description(Session)] string? session = null,
        CancellationToken ct = default)
    {
        using IDisposable scope = Sessions.Use(session);
        try { return Render(await Soak.RunAsync(target, Math.Clamp(actions, 1, 500), seed, Math.Clamp(radius, 2, 500), Math.Clamp(pause, 0, 30), Math.Clamp(desyncSamples, 1, 60), ct, filter)); }
        catch (Exception e) when (e is AgentException or ProbeCompileException)
        {
            return Render(new JsonObject { ["ok"] = false, ["error"] = e.Message });
        }
    }

    [McpServerTool(Name = "dvmp_trace")]
    [Description("""
        Mutation tracer: answers "what code did this to the item?". On first use it rewrites
        every call site in the game and Multiplayer that changes physics freeze, colliders,
        active state, parent, position, velocity, sleep, or destroys an object, so calls on
        networked items are recorded with the calling method (takes a few seconds; stays
        installed until the game restarts; later calls also refresh which items are watched,
        e.g. items spawned since). Then returns the recorded history per item.
        Use it on items the desync checker flags (e.g. health.below-ground) on each peer.
        """)]
    public static async Task<string> Trace(
        [Description("host, client, all (default), or a pid.")] string target = "all",
        [Description("Comma-separated item NetIds to show. Default: every item with history.")] string? ids = null,
        [Description("Most recent events per item. Default 20.")] int last = 20,
        [Description(Session)] string? session = null,
        CancellationToken ct = default)
    {
        using IDisposable scope = Sessions.Use(session);
        try { return Render(await Tracer.RunAsync(target, ids, Math.Clamp(last, 1, 60), ct)); }
        catch (Exception e) when (e is AgentException or ProbeCompileException)
        {
            return Render(new JsonObject { ["ok"] = false, ["error"] = e.Message });
        }
    }

    [McpServerTool(Name = "dvmp_eval")]
    [Description("""
        Compiles C# and runs it inside running game process(es) on Unity's main thread,
        returning the result as JSON per process. Runs on host and client in parallel
        when target is "all", which is the way to compare state across peers.

        The code can be:
        - an expression: `PlayerManager.PlayerTransform.position`
        - a statement body ending in `return ...;`
        - a coroutine body using `yield return` (e.g. `yield return new WaitForSeconds(2);`);
          set `ctx.Result = ...` to report a value when it finishes
        - a full `public static class Probe { public static object Run(DevContext ctx) {...} }`

        Coroutines: run parallel work with `ctx.StartCoroutine(routine)`, never
        `someBehaviour.StartCoroutine(...)`. Everything a probe starts is stopped when it
        returns, fails or times out (a direct StartCoroutine is caught and stopped too, with a
        warning). A watcher that must outlive the probe: `ctx.StartBackground("name", pc =>
        Watch(pc), maxSeconds)` (default 600, max 3600); it can `pc.Log(x)`, later probes find
        it with `DevContext.Background("name")`, and dvmp_probes lists or stops it.

        In scope: System, System.Linq, System.Collections(.Generic), UnityEngine, and
        Dvmp.Agent. `ctx` is the DevContext: ctx.Log(x) appends to output, ctx.Args holds
        string args, DevContext.State is a dictionary that persists between calls in that
        process, ctx.Role is host/client/offline.
        Helpers: Dev.Abs(worldPos) removes this process's origin shift (always use absolute
        positions when comparing peers), Dev.World(abs), Dev.PlayerAbs, Dev.Path(component),
        Dev.All<T>() (incl. inactive), Dev.Near<T>(radius), Dev.Stub(unityObject).

        All game assemblies (Assembly-CSharp, DV.*) and Multiplayer.dll are referenced with
        private members made public, so private fields/methods can be used directly. Add
        namespaces with `usings` (e.g. "using DV.CabControls; using Multiplayer.Server.Items.LiveState; using Multiplayer.Shared.Items.LiveState; using Multiplayer.Shared.Jobs; using Multiplayer.Shared.Stations; using Multiplayer.Shared.World;").
        Return small anonymous objects/lists; Unity objects are reduced to {type,name,id,path}
        and Quaternions to Euler angles. Results over 512 KB are truncated.
        """)]
    public static async Task<string> Eval(
        [Description("C# expression, statement body, coroutine body, or Probe class.")] string code,
        [Description("host, client, all, a pid, or remote:NAME (a paired game on someone else's PC; never included in all). Default all.")] string target = "all",
        [Description("auto (default), expression, body, coroutine, or class.")] string mode = "auto",
        [Description("Extra using directives, e.g. \"using DV.Logic.Job;\".")] string? usings = null,
        [Description("Optional string arguments, available as ctx.Args.")] Dictionary<string, string>? args = null,
        [Description("Seconds before the probe is abandoned. Default 30.")] int timeoutSeconds = 30,
        [Description(Session)] string? session = null,
        CancellationToken ct = default)
    {
        using IDisposable scope = Sessions.Use(session);
        try { return Render(await Operations.EvalAsync(code, target, mode, usings, args, Math.Clamp(timeoutSeconds, 1, 600), ct)); }
        catch (AgentException e) { return Render(new JsonObject { ["ok"] = false, ["error"] = e.Message }); }
    }

    [McpServerTool(Name = "dvmp_probes")]
    [Description("""
        Lists what probes left running in each game: coroutines (with their id, kind, age,
        host object and their own log), recently ended ones and why, Harmony patches by owner
        id ("dvmp.*" or from a probe assembly), and how many probe assemblies are loaded
        (Mono can't unload them). Optionally stops coroutines or removes patches first.
        Patches of dvmp.live.* (live hotfixes) and dvmp.agent.* are never removed; dvmp.harness.*
        ones only when named exactly. Use it before and after probing a game other people play in.
        """)]
    public static async Task<string> Probes(
        [Description("host, client, all, a pid, or remote:NAME. Default all.")] string target = "all",
        [Description("Stop coroutines: \"all\", an id, a name, or a probe id prefix (dvmp_probe_...).")] string? stop = null,
        [Description("Harmony owner ids to unpatch; \"all\" means every probe owner, \"dvmp.foo.*\" a prefix.")] string[]? unpatch = null,
        [Description(Session)] string? session = null,
        CancellationToken ct = default)
    {
        using IDisposable scope = Sessions.Use(session);
        try { return Render(await Operations.ProbesAsync(target, stop, unpatch, ct)); }
        catch (AgentException e) { return Render(new JsonObject { ["ok"] = false, ["error"] = e.Message }); }
    }

    [McpServerTool(Name = "dvmp_screenshot", ReadOnly = true)]
    [Description("""
        Captures what each game window is currently showing, as an image per process. Use it
        to visually confirm things probes can't easily express (is the item actually on the
        train, is the UI open, did the car derail).
        """)]
    public static async Task<CallToolResult> Screenshot(
        [Description("host, client, all, a pid, or remote:NAME (a paired game on someone else's PC; never included in all). Default all.")] string target = "all",
        [Description("Downscale to at most this width in pixels. Default 1024.")] int maxWidth = 1024,
        [Description(Session)] string? session = null,
        CancellationToken ct = default)
    {
        using IDisposable scope = Sessions.Use(session);
        CallToolResult result = new();
        try
        {
            foreach (var (agent, shot, error) in await Operations.ScreenshotAsync(target, Math.Clamp(maxWidth, 160, 3840), ct))
            {
                if (shot == null)
                {
                    result.Content.Add(new TextContentBlock { Text = $"{agent.Label}: screenshot failed: {error}" });
                    continue;
                }
                result.Content.Add(new TextContentBlock { Text = $"{agent.Label} ({shot.Width}x{shot.Height})" });
                result.Content.Add(ImageContentBlock.FromBytes(shot.Jpeg, "image/jpeg"));
            }
        }
        catch (AgentException e)
        {
            result.IsError = true;
            result.Content.Add(new TextContentBlock { Text = e.Message });
        }
        return result;
    }

    [McpServerTool(Name = "dvmp_logs", ReadOnly = true)]
    [Description("""
        Returns Unity log messages (including Multiplayer's own log lines and exceptions with
        stack traces) from each process. By default only returns entries newer than the
        previous dvmp_logs call for that process.
        """)]
    public static async Task<string> Logs(
        [Description("host, client, all, a pid, or remote:NAME (a paired game on someone else's PC; never included in all). Default all.")] string target = "all",
        [Description("log, warning, error, or exception. Default warning.")] string minLevel = "warning",
        [Description("Only entries whose message contains this text (case-insensitive).")] string? contains = null,
        [Description("Maximum entries per process (newest kept). Default 100.")] int max = 100,
        [Description("Only entries since the previous call. Default true; false returns the whole buffer.")] bool sinceLast = true,
        [Description(Session)] string? session = null,
        CancellationToken ct = default)
    {
        using IDisposable scope = Sessions.Use(session);
        try { return Render(await Operations.LogsAsync(target, minLevel, contains, Math.Clamp(max, 1, 2000), sinceLast, ct)); }
        catch (AgentException e) { return Render(new JsonObject { ["ok"] = false, ["error"] = e.Message }); }
    }

    [McpServerTool(Name = "dvmp_perf", ReadOnly = true)]
    [Description("""
        Measures each game for a few seconds: frame times, memory, physics, players, and packets
        sent and received per type. Use it to find what a stall or a busy connection is made of.
        """)]
    public static async Task<string> Perf(
        [Description("host, client, all, a pid, or remote:NAME. Default all.")] string target = "all",
        [Description("Seconds to measure. Default 10.")] int seconds = 10,
        [Description(Session)] string? session = null,
        CancellationToken ct = default)
    {
        using IDisposable scope = Sessions.Use(session);
        seconds = Math.Clamp(seconds, 1, 120);
        try
        {
            return Render(await Operations.EvalAsync(Dvmp.Session.LoadProbe("perf"), target, "class", null,
                new Dictionary<string, string> { ["seconds"] = seconds.ToString() }, seconds + 30, ct));
        }
        catch (Exception e) when (e is AgentException or ProbeCompileException)
        {
            return Render(new JsonObject { ["ok"] = false, ["error"] = e.Message });
        }
    }

    [McpServerTool(Name = "dvmp_remote")]
    [Description("""
        Remote debugging of a game on someone else's PC, over Tailscale (DevTools/README.md, "Remote
        debugging"). Once paired and connected, every tool that takes a target accepts remote:NAME.
        - list: the paired games.
        - pair: name, address (host or host:port) and the pairing code their game shows; once per PC.
        - connect: name and the session code their game shows (a new one every launch of their game).
        - forget: drop the pairing on this PC (it stays in their game until they remove it).
        """)]
    public static async Task<string> Remote(
        [Description("list (default), pair, connect, or forget.")] string action = "list",
        [Description("The name this PC knows the remote game by.")] string? name = null,
        [Description("pair: their address, host or host:port.")] string? address = null,
        [Description("pair: the pairing code; connect: the session code.")] string? code = null,
        [Description("pair: how this PC is labelled in their game.")] string? label = null)
    {
        try
        {
            switch (action)
            {
                case "pair" when name != null && address != null && code != null:
                    return Render(await Remotes.PairAsync(name, address, code, label));
                case "connect" when name != null && code != null:
                    return Render(await Remotes.ConnectAsync(name, code));
                case "forget" when name != null:
                    return Render(new JsonObject { ["ok"] = Remotes.Forget(name), ["forgot"] = name,
                        ["note"] = "the pairing still exists in their game until they remove it there" });
                case "list":
                    return Render(new JsonObject
                    {
                        ["remotes"] = new JsonArray(Remotes.All().Select(r => (JsonNode)new JsonObject
                        {
                            ["target"] = Remotes.TargetPrefix + r.Name,
                            ["address"] = $"{r.Address}:{r.Port}",
                            ["pairedUtc"] = r.PairedUtc,
                            ["hasSessionCode"] = r.SessionCode != null,
                        }).ToArray())
                    });
                default:
                    return Render(new JsonObject { ["ok"] = false, ["error"] = "action: list | pair (name, address, code) | connect (name, code) | forget (name)" });
            }
        }
        catch (AgentException e) { return Render(new JsonObject { ["ok"] = false, ["error"] = e.Message }); }
    }

    internal static string Render(JsonNode node) => node.ToJsonString(pretty);
}
