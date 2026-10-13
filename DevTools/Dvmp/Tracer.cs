using System.Text.Json.Nodes;

namespace Dvmp;

/// <summary>Installs (or refreshes) the in-game mutation tracer and reads its history.</summary>
internal static class Tracer
{
    public static async Task<JsonObject> RunAsync(string? target, string? ids, int last, CancellationToken ct)
    {
        const string usings = "using Multiplayer.Components.Networking.World;";
        JsonObject install = await Operations.EvalAsync(Session.LoadProbe("trace-install"), target, "class", usings, null, 180, ct);
        if (install["compileError"] is { } compile) return new JsonObject { ["ok"] = false, ["compileError"] = compile.DeepClone() };

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
