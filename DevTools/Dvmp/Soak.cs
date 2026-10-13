using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dvmp;

/// <summary>Runs the soak bot on one or both peers, then the desync checker.</summary>
internal static class Soak
{
    public static async Task<JsonObject> RunAsync(string? target, int actions, int seed, double radius, double pause,
        int desyncSamples, CancellationToken ct)
    {
        string probe = Session.LoadProbe("soak");
        List<Agent> agents = await Agents.ResolveAsync(target ?? "client", ct);
        int timeoutSeconds = (int)Math.Min(3600, actions * (pause + 15) + 60);

        // Each peer gets its own seed so host and client do different things when run together.
        var runs = await Task.WhenAll(agents.Select(async (agent, index) =>
        {
            Dictionary<string, string> args = new()
            {
                ["seed"] = (seed + index * 1000).ToString(),
                ["actions"] = actions.ToString(),
                ["radius"] = radius.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["pause"] = pause.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            JsonObject r = await Operations.EvalAsync(probe, agent.Pid.ToString(), "class", null, args, timeoutSeconds, ct);
            return (agent, r);
        }));

        JsonObject summary = new();
        JsonArray failures = new();
        JsonObject log = new();
        foreach (var (agent, r) in runs)
        {
            if (r["compileError"] is { } compile) return new JsonObject { ["ok"] = false, ["compileError"] = compile.DeepClone() };
            JsonNode? one = r["results"]?.AsObject().FirstOrDefault().Value;
            summary[agent.Label] = one?["ok"]?.GetValue<bool>() == true ? one["result"]?.DeepClone() : new JsonObject { ["error"] = one?["error"]?.DeepClone() };
            JsonArray steps = one?["output"] as JsonArray ?? new JsonArray();
            log[agent.Label] = steps.DeepClone();
            foreach (JsonNode? step in steps)
                if (step?["error"] is JsonValue e && e.TryGetValue(out string? message) && message != null)
                {
                    JsonObject failure = (JsonObject)step.DeepClone();
                    failure["peer"] = agent.Label;
                    failures.Add(failure);
                }
        }

        await Task.Delay(TimeSpan.FromSeconds(3), ct); // let the last throws land
        JsonObject desync = await Desync.RunAsync(desyncSamples, 1, 40, ct);

        string dir = Path.Combine(Paths.DataRoot, "reports");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"soak-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, new JsonObject { ["soak"] = summary.DeepClone(), ["actions"] = log, ["desyncReport"] = desync["reportPath"]?.DeepClone() }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);

        return new JsonObject
        {
            ["soak"] = summary,
            ["actionFailures"] = failures,
            ["desync"] = desync,
            ["soakLogPath"] = path
        };
    }
}
