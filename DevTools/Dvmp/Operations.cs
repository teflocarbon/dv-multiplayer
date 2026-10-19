using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Dvmp;

internal sealed record Screenshot(Agent Agent, byte[] Jpeg, int Width, int Height);

/// <summary>The harness operations; the MCP tools and the CLI are thin wrappers over these.</summary>
internal static class Operations
{
    // Per-process log cursors so repeated log calls only return what is new. Keyed by label:
    // remote games have no local pid.
    private static readonly ConcurrentDictionary<string, long> logCursors = new();

    public static async Task<JsonObject> StatusAsync(CancellationToken ct = default, bool all = false)
    {
        List<Agent> agents = await Agents.DiscoverAsync(ct, all);
        JsonArray list = new();
        JsonArray warnings = new();
        foreach (Agent agent in agents)
        {
            JsonObject entry = agent.Status?.DeepClone() as JsonObject ?? new JsonObject();
            entry.Remove("ok");
            entry["pid"] = agent.Pid;
            entry["port"] = agent.Info.Port;
            entry["session"] = agent.Session;
            if (agent.StatusError != null) entry["error"] = agent.StatusError;
            foreach (string w in ProbeWarnings(entry["probes"] as JsonObject))
                warnings.Add($"{agent.Label}: {w}");
            list.Add(entry);
        }
        // Paired remote games are listed, not contacted: a status call is a request in their audit log.
        JsonArray remotes = new();
        foreach (RemoteInfo remote in Remotes.All())
            remotes.Add(new JsonObject
            {
                ["target"] = Remotes.TargetPrefix + remote.Name,
                ["address"] = $"{remote.Address}:{remote.Port}",
                ["hasSessionCode"] = remote.SessionCode != null
            });
        return new JsonObject
        {
            ["warnings"] = warnings.Count > 0 ? warnings : null,
            ["agents"] = list,
            ["remotes"] = remotes.Count > 0 ? remotes : null,
            ["session"] = all ? "(all)" : Sessions.Current,
            ["gameDir"] = Paths.GameDir,
            ["hint"] = agents.Count == 0 ? "no game running with the dev agent" : null
        };
    }

    // Coroutines or Harmony patches a probe left behind are easy to forget, and they degrade a
    // game other people may be playing in (a forgotten watcher once hitched a live host for 50 minutes).
    private static List<string> ProbeWarnings(JsonObject? probes)
    {
        List<string> warnings = new();
        if (probes == null) return warnings;
        if (probes["lingering"] is JsonArray lingering && lingering.Count > 0)
            warnings.Add($"{lingering.Count} probe coroutine(s) running past their probe: " +
                string.Join(", ", lingering.Select(c => $"#{c?["id"]} {c?["name"]} ({c?["kind"]}, {c?["ageSeconds"]} s" +
                    (c?["maxSeconds"] is JsonValue max ? $" of {max}" : "") + ")")) +
                "; inspect with dvmp_probes, stop with dvmp_probes stop=<id|all>");
        if (probes["patches"] is JsonArray patches)
        {
            List<string> owners = patches.Where(p => p?["category"]?.GetValue<string>() == "probe")
                .Select(p => $"{p?["owner"]} ({p?["methods"]} methods)").ToList();
            if (owners.Count > 0)
                warnings.Add("Harmony patches from probes still installed: " + string.Join(", ", owners) +
                    "; remove with dvmp_probes unpatch=<owner|all>");
        }
        return warnings;
    }

    public static async Task<JsonObject> ProbesAsync(string? target, string? stop, IReadOnlyList<string>? unpatch, CancellationToken ct = default)
    {
        List<Agent> agents = await Agents.ResolveAsync(target, ct);
        JsonObject body = new() { ["stop"] = stop };
        if (unpatch is { Count: > 0 }) body["unpatch"] = new JsonArray(unpatch.Select(u => (JsonNode?)u).ToArray());
        JsonObject results = new();
        await Task.WhenAll(agents.Select(async agent =>
        {
            JsonNode node;
            try
            {
                JsonObject r = await Agents.CallAsync(agent, "/v1/probes", (JsonObject)body.DeepClone(), TimeSpan.FromSeconds(15), ct);
                r.Remove("ok");
                node = r;
            }
            catch (AgentException e) { node = new JsonObject { ["error"] = e.Message }; }
            lock (results) results[agent.Label] = node;
        }));
        return results;
    }

    public static async Task<JsonObject> EvalAsync(string code, string? target, string? mode, string? usings,
        Dictionary<string, string>? args, int timeoutSeconds, CancellationToken ct = default)
    {
        CompiledProbe probe;
        try { probe = ProbeCompiler.Compile(code, mode ?? "auto", usings); }
        catch (ProbeCompileException e) { return new JsonObject { ["ok"] = false, ["compileError"] = e.Message }; }

        List<Agent> agents = await Agents.ResolveAsync(target, ct);
        JsonObject body = new()
        {
            ["assembly"] = Convert.ToBase64String(probe.Assembly),
            ["type"] = probe.TypeName,
            ["timeoutMs"] = timeoutSeconds * 1000,
            // For a remote game's audit log; the agent runs the assembly, not this.
            ["source"] = string.IsNullOrWhiteSpace(usings) ? code : usings + "\n" + code
        };
        if (args is { Count: > 0 })
        {
            JsonObject a = new();
            foreach (var (k, v) in args) a[k] = v;
            body["args"] = a;
        }

        JsonObject results = new();
        await Task.WhenAll(agents.Select(async agent =>
        {
            JsonNode node;
            try
            {
                JsonObject r = await Agents.CallAsync(agent, "/v1/eval", (JsonObject)body.DeepClone(),
                    TimeSpan.FromSeconds(timeoutSeconds + 10), ct);
                r.Remove("role");
                node = r;
            }
            catch (AgentException e) { node = new JsonObject { ["ok"] = false, ["error"] = e.Message }; }
            lock (results) results[agent.Label] = node;
        }));
        return new JsonObject { ["mode"] = probe.Mode, ["results"] = results };
    }

    public static async Task<List<(Agent Agent, Screenshot? Shot, string? Error)>> ScreenshotAsync(
        string? target, int maxWidth, CancellationToken ct = default)
    {
        List<Agent> agents = await Agents.ResolveAsync(target, ct);
        var shots = await Task.WhenAll(agents.Select(async agent =>
        {
            try
            {
                Task<JsonObject> Capture() => Agents.CallAsync(agent, "/v1/screenshot",
                    new JsonObject { ["maxWidth"] = maxWidth }, TimeSpan.FromSeconds(20), ct);
                // A remote game's window is on their PC: capture it as it is.
                JsonObject r = agent.Remote != null ? await Capture() : await Win32.WithWindowVisibleAsync(agent.Pid, Capture);
                byte[] jpeg = Convert.FromBase64String(r["base64"]!.GetValue<string>());
                return (agent, (Screenshot?)new Screenshot(agent, jpeg, r["width"]!.GetValue<int>(), r["height"]!.GetValue<int>()), (string?)null);
            }
            catch (Exception e) { return (agent, (Screenshot?)null, (string?)e.Message); }
        }));
        return shots.ToList();
    }

    public static async Task<JsonObject> LogsAsync(string? target, string? minLevel, string? contains, int max,
        bool sinceLast, CancellationToken ct = default)
    {
        List<Agent> agents = await Agents.ResolveAsync(target, ct);
        JsonObject results = new();
        await Task.WhenAll(agents.Select(async agent =>
        {
            long since = sinceLast ? logCursors.GetValueOrDefault(agent.Label) : 0;
            JsonNode node;
            try
            {
                JsonObject r = await Agents.CallAsync(agent, "/v1/logs", new JsonObject
                {
                    ["sinceSeq"] = since,
                    ["minLevel"] = minLevel ?? "log",
                    ["contains"] = contains,
                    ["max"] = max
                }, TimeSpan.FromSeconds(10), ct);
                if (r["lastSeq"] is JsonValue last) logCursors[agent.Label] = last.GetValue<long>();
                r.Remove("ok");
                node = r;
            }
            catch (AgentException e) { node = new JsonObject { ["error"] = e.Message }; }
            lock (results) results[agent.Label] = node;
        }));
        return results;
    }
}
