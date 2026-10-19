using System.Text.Json.Nodes;

namespace Dvmp;

/// <summary>Installs (or refreshes) the in-game mutation tracer and reads its history.</summary>
internal static class Tracer
{
    public static async Task<JsonObject> RunAsync(string? target, string? ids, int last, CancellationToken ct)
    {
        const string usings = "using Multiplayer.Server.Items; using Multiplayer.Shared.Items; using Multiplayer.Shared.Jobs; using Multiplayer.Shared.Stations; using Multiplayer.Shared.World;";
        // Install one game at a time: patching both at once collides on Harmony's temp files.
        JsonObject install = new() { ["results"] = new JsonObject() };
        foreach (Agent agent in await Agents.ResolveAsync(target, ct))
        {
            JsonObject one = await Operations.EvalAsync(Session.LoadProbe("trace-install"), agent.Pid.ToString(), "class", usings, null, 180, ct);
            if (one["compileError"] is { } compile) return new JsonObject { ["ok"] = false, ["compileError"] = compile.DeepClone() };
            foreach (var (label, node) in one["results"]!.AsObject())
                install["results"]![label] = node?.DeepClone();
        }

        Dictionary<string, string> args = new() { ["last"] = last.ToString() };
        if (!string.IsNullOrWhiteSpace(ids)) args["ids"] = ids.Replace(" ", string.Empty);
        JsonObject show = await Operations.EvalAsync(Session.LoadProbe("trace-show"), target, "class", null, args, 30, ct);

        JsonObject result = new();
        foreach (var (label, node) in show["results"]!.AsObject())
        {
            JsonObject peer = new()
            {
                ["install"] = install["results"]?[label]?["result"]?.DeepClone() ?? install["results"]?[label]?["error"]?.DeepClone(),
                ["items"] = node?["result"]?["items"]?.DeepClone() ?? node?["error"]?.DeepClone()
            };
            result[label] = peer;
        }
        return result;
    }
}
