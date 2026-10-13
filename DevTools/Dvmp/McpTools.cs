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

    [McpServerTool(Name = "dvmp_status", ReadOnly = true, Idempotent = true)]
    [Description("""
        Lists running Derail Valley processes that have the DVMP dev agent, with their
        multiplayer role (host/client/offline), scene, whether the world is loaded, fps,
        and the local player's absolute position. Call this first.
        """)]
    public static async Task<string> Status(CancellationToken ct) =>
        Render(await Operations.StatusAsync(ct));

    [McpServerTool(Name = "dvmp_up")]
    [Description("""
        Ensures a host and a client game instance are running in desktop mode and joined to
        each other over loopback, launching them if needed (reuses a healthy running pair).
        A cold start takes a minute or more: this waits up to waitSeconds and, if not done,
        returns the current stage; call it again to keep waiting on the same launch.
        The host loads the save shown in its launcher unless `save` names one. Test sessions
        are never advertised to the lobby server or Steam lobbies.
        """)]
    public static async Task<string> Up(
        [Description("Restart both games even if a healthy session is running.")] bool fresh = false,
        [Description("Save name or UID for the host to load. Default: the launcher's current save.")] string? save = null,
        [Description("Seconds to wait before returning progress. Default 50.")] int waitSeconds = 50,
        [Description("Start minimised at below-normal priority (default true). Games launched by the harness are always muted and run at low graphics settings.")] bool background = true,
        [Description("Frame cap while a game window is focused. Default 30; unfocused games run at 20.")] int fpsCap = 30,
        [Description("Leave game audio on. Default false.")] bool sound = false,
        CancellationToken ct = default) =>
        Render(await Session.UpAsync(new UpOptions(fresh, save, 960, 540, Arrange: true, background, Math.Clamp(fpsCap, 5, 240), sound),
            TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 600)), ct));

    [McpServerTool(Name = "dvmp_down", Destructive = true)]
    [Description("Quits every game instance that has the dev agent (kills any that don't exit within 20s).")]
    public static async Task<string> Down(CancellationToken ct) => Render(await Session.DownAsync(ct));

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
        CancellationToken ct = default)
    {
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
        CancellationToken ct = default)
    {
        try { return Render(await Soak.RunAsync(target, Math.Clamp(actions, 1, 500), seed, Math.Clamp(radius, 2, 500), Math.Clamp(pause, 0, 30), Math.Clamp(desyncSamples, 1, 60), ct)); }
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

        In scope: System, System.Linq, System.Collections(.Generic), UnityEngine, and
        Dvmp.Agent. `ctx` is the DevContext: ctx.Log(x) appends to output, ctx.Args holds
        string args, DevContext.State is a dictionary that persists between calls in that
        process, ctx.Role is host/client/offline.
        Helpers: Dev.Abs(worldPos) removes this process's origin shift (always use absolute
        positions when comparing peers), Dev.World(abs), Dev.PlayerAbs, Dev.Path(component),
        Dev.All<T>() (incl. inactive), Dev.Near<T>(radius), Dev.Stub(unityObject).

        All game assemblies (Assembly-CSharp, DV.*) and Multiplayer.dll are referenced with
        private members made public, so private fields/methods can be used directly. Add
        namespaces with `usings` (e.g. "using DV.CabControls; using Multiplayer.Components.Networking.World;").
        Return small anonymous objects/lists; Unity objects are reduced to {type,name,id,path}
        and Quaternions to Euler angles. Results over 512 KB are truncated.
        """)]
    public static async Task<string> Eval(
        [Description("C# expression, statement body, coroutine body, or Probe class.")] string code,
        [Description("host, client, all, or a pid. Default all.")] string target = "all",
        [Description("auto (default), expression, body, coroutine, or class.")] string mode = "auto",
        [Description("Extra using directives, e.g. \"using DV.Logic.Job;\".")] string? usings = null,
        [Description("Optional string arguments, available as ctx.Args.")] Dictionary<string, string>? args = null,
        [Description("Seconds before the probe is abandoned. Default 30.")] int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        try { return Render(await Operations.EvalAsync(code, target, mode, usings, args, Math.Clamp(timeoutSeconds, 1, 600), ct)); }
        catch (AgentException e) { return Render(new JsonObject { ["ok"] = false, ["error"] = e.Message }); }
    }

    [McpServerTool(Name = "dvmp_screenshot", ReadOnly = true)]
    [Description("""
        Captures what each game window is currently showing, as an image per process. Use it
        to visually confirm things probes can't easily express (is the item actually on the
        train, is the UI open, did the car derail).
        """)]
    public static async Task<CallToolResult> Screenshot(
        [Description("host, client, all, or a pid. Default all.")] string target = "all",
        [Description("Downscale to at most this width in pixels. Default 1024.")] int maxWidth = 1024,
        CancellationToken ct = default)
    {
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
        [Description("host, client, all, or a pid. Default all.")] string target = "all",
        [Description("log, warning, error, or exception. Default warning.")] string minLevel = "warning",
        [Description("Only entries whose message contains this text (case-insensitive).")] string? contains = null,
        [Description("Maximum entries per process (newest kept). Default 100.")] int max = 100,
        [Description("Only entries since the previous call. Default true; false returns the whole buffer.")] bool sinceLast = true,
        CancellationToken ct = default)
    {
        try { return Render(await Operations.LogsAsync(target, minLevel, contains, Math.Clamp(max, 1, 2000), sinceLast, ct)); }
        catch (AgentException e) { return Render(new JsonObject { ["ok"] = false, ["error"] = e.Message }); }
    }

    internal static string Render(JsonNode node) => node.ToJsonString(pretty);
}
