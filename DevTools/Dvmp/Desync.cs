using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dvmp;

/// <summary>
/// Cross-peer desync checker: snapshots actual Unity state on host and client several times,
/// compares them, and reports divergences that persist across most samples (so in-flight
/// latency does not count as desync).
/// </summary>
internal static class Desync
{
    private sealed record Finding(string Kind, string Id, string Name, string Host, string Client);

    private sealed class Tally
    {
        public required Finding First;
        public Finding Last = null!;
        public int Count;
    }

    // Order in which kinds are reported: structural problems first, cosmetic last.
    private static readonly string[] KindOrder =
    {
        "health.no-collision", "health.below-ground", "authority.record-mismatch",
        "item.missing-on-client", "item.ghost-on-client", "item.unexpected-on-client",
        "car.missing-on-client", "car.ghost-on-client", "car.coupling", "car.derailed",
        "world.money", "world.junction",
        "item.state", "item.parent", "item.active", "item.revision", "item.position", "item.kinematic",
        "car.position", "car.track", "car.handbrake", "car.brake-pipe"
    };

    public static async Task<JsonObject> RunAsync(int samples, double intervalSeconds, int top, CancellationToken ct)
    {
        List<Agent> agents = await Agents.DiscoverAsync(ct);
        Agent host = agents.FirstOrDefault(a => a.Role == "host") ?? throw new AgentException("no host running (dvmp_up first)");
        Agent client = agents.FirstOrDefault(a => a.Role == "client") ?? throw new AgentException("no client running (dvmp_up first)");

        string probe = Session.LoadProbe("snapshot");
        CompiledProbe compiled = ProbeCompiler.Compile(probe, "class");
        Dictionary<string, Tally> tallies = new();
        JsonObject? lastHost = null, lastClient = null;
        List<double> captureMs = new();

        for (int i = 0; i < samples; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Task<JsonObject> h = Snapshot(host, compiled, ct);
            Task<JsonObject> c = Snapshot(client, compiled, ct);
            await Task.WhenAll(h, c);
            captureMs.Add(sw.Elapsed.TotalMilliseconds);
            lastHost = h.Result;
            lastClient = c.Result;

            foreach (Finding f in Compare(lastHost, lastClient))
            {
                string key = f.Kind + "|" + f.Id;
                if (!tallies.TryGetValue(key, out Tally? tally)) tallies[key] = tally = new Tally { First = f };
                tally.Last = f;
                tally.Count++;
            }

            if (i < samples - 1) await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }

        int threshold = Math.Max(1, (int)Math.Ceiling(samples * 0.8));
        var persistent = tallies.Values.Where(t => t.Count >= threshold).ToList();
        var transient = tallies.Values.Where(t => t.Count < threshold).ToList();

        JsonObject report = new()
        {
            ["samples"] = samples,
            ["intervalSeconds"] = intervalSeconds,
            ["persistentThreshold"] = $"present in >= {threshold}/{samples} samples",
            ["captureMsAvg"] = Math.Round(captureMs.Average(), 0),
            ["counts"] = Counts(lastHost!, lastClient!),
            ["persistent"] = CountByKind(persistent),
            ["transient"] = CountByKind(transient),
            ["top"] = new JsonArray(Ordered(persistent).Take(top).Select(Describe).ToArray()),
        };

        string dir = Path.Combine(Paths.DataRoot, "reports");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"desync-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        JsonObject full = (JsonObject)report.DeepClone();
        full["allPersistent"] = new JsonArray(Ordered(persistent).Select(Describe).ToArray());
        full["allTransient"] = new JsonArray(Ordered(transient).Select(Describe).ToArray());
        await File.WriteAllTextAsync(path, full.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        report["reportPath"] = path;
        return report;
    }

    private static async Task<JsonObject> Snapshot(Agent agent, CompiledProbe probe, CancellationToken ct)
    {
        JsonObject body = new()
        {
            ["assembly"] = Convert.ToBase64String(probe.Assembly),
            ["type"] = probe.TypeName,
            ["timeoutMs"] = 15000
        };
        JsonObject r = await Agents.CallAsync(agent, "/v1/eval", body, TimeSpan.FromSeconds(25), ct);
        if (r["ok"]?.GetValue<bool>() != true) throw new AgentException($"{agent.Label}: snapshot failed: {r["error"]}");
        return r["result"] as JsonObject ?? throw new AgentException($"{agent.Label}: snapshot returned nothing");
    }

    // ---------------------------------------------------------------- comparison

    private static IEnumerable<Finding> Compare(JsonObject host, JsonObject client)
    {
        foreach (Finding f in Health(host, "host")) yield return f;
        foreach (Finding f in Health(client, "client")) yield return f;
        foreach (Finding f in CompareItems(host, client)) yield return f;
        foreach (Finding f in CompareCars(host, client)) yield return f;
        foreach (Finding f in CompareWorld(host, client)) yield return f;
    }

    // Single-peer problems that are bugs regardless of what the other peer shows: an item lying
    // in the world with every collider disabled (it falls through the floor), or underground.
    private static IEnumerable<Finding> Health(JsonObject snapshot, string peer)
    {
        foreach (var (id, item) in ById(snapshot["items"]))
        {
            if (peer == "host" && item["rp"] is JsonValue && RecordMismatch(item) is { } mismatch)
                yield return new Finding("authority.record-mismatch", "host:" + id, S(item, "n"), mismatch, "-");

            if (S(item, "a") != "true" || S(item, "p") == "player") continue;
            string where = $"{peer}: {S(item, "s")} {S(item, "p")}";
            if (F(item, "ct") > 0 && F(item, "cl") == 0)
                yield return new Finding("health.no-collision", peer + ":" + id, S(item, "n"),
                    peer == "host" ? where + $", 0/{S(item, "ct")} colliders enabled" : "-",
                    peer == "client" ? where + $", 0/{S(item, "ct")} colliders enabled" : "-");
            if (item["bt"] is JsonValue below && below.TryGetValue(out double metres) && metres > 0.5)
                yield return new Finding("health.below-ground", peer + ":" + id, S(item, "n"),
                    peer == "host" ? $"{where}, {metres:0.#} m underground" : "-",
                    peer == "client" ? $"{where}, {metres:0.#} m underground" : "-");
        }
    }

    // The host's authority record vs. what the host's own Unity object is actually doing.
    private static string? RecordMismatch(JsonObject item)
    {
        string record = S(item, "rp"), state = S(item, "s");
        bool active = S(item, "a") == "true";
        bool ok = record switch
        {
            "World" or "TrainInterior" or "StaticParent" => state is "Dropped" or "Thrown",
            "PlayerHand" => state == "InHand",
            "PlayerInventory" => state == "InInventory",
            "Attached" or "SnappedAttachment" => state is "Attached" or "Removed",
            "LostAndFound" or "Container" or "Destroyed" => !active,
            _ => true
        };
        return ok ? null : $"record {record}, but the object is {state}{(active ? " and active" : " (hidden)")} under {S(item, "p")}";
    }

    private static IEnumerable<Finding> CompareItems(JsonObject host, JsonObject client)
    {
        Dictionary<int, JsonObject> h = ById(host["items"]);
        Dictionary<int, JsonObject> c = ById(client["items"]);
        HashSet<int> known = KnownByHost(host, client);

        foreach (int id in known.Where(id => !c.ContainsKey(id)))
            yield return new Finding("item.missing-on-client", id.ToString(), Name(h, id),
                "host believes the client holds it", "absent");
        foreach (var (id, item) in c)
        {
            if (!h.ContainsKey(id))
                yield return new Finding("item.ghost-on-client", id.ToString(), S(item, "n"), "absent", Brief(item));
            else if (!known.Contains(id))
                yield return new Finding("item.unexpected-on-client", id.ToString(), S(item, "n"),
                    "not in client's interest set", Brief(item));
        }

        foreach (var (id, hi) in h)
        {
            if (!c.TryGetValue(id, out JsonObject? ci)) continue;
            string name = S(hi, "n");
            bool nearBoth = F(hi, "d") is >= 0 and < 100 && F(ci, "d") is >= 0 and < 100;
            if (nearBoth && S(hi, "a") != S(ci, "a"))
                yield return new Finding("item.active", id.ToString(), name, S(hi, "a"), S(ci, "a"));
            if (S(hi, "s") != S(ci, "s")) yield return new Finding("item.state", id.ToString(), name, S(hi, "s"), S(ci, "s"));
            if (S(hi, "rev") != S(ci, "rev")) yield return new Finding("item.revision", id.ToString(), name, S(hi, "rev"), S(ci, "rev"));

            string hp = S(hi, "p"), cp = S(ci, "p");
            if (hp != cp)
            {
                yield return new Finding("item.parent", id.ToString(), name, hp, cp);
                continue;
            }
            if (hp == "player") continue;

            if (Vec(hi["pos"]) is { } a && Vec(ci["pos"]) is { } b)
            {
                double speed = Math.Max(F(hi, "v"), F(ci, "v"));
                double distance = Dist(a, b);
                if (speed < 0.2 && distance > 0.3)
                    yield return new Finding("item.position", id.ToString(), name, Fmt(a), $"{Fmt(b)} ({distance:0.##} m off)");
            }
            // Vanilla freezes items far from each peer's own camera (RespawnOnDrop.Checker), so
            // kinematic state is only comparable when the item is near both players.
            if (nearBoth && hi["k"] is JsonValue hk && ci["k"] is JsonValue ck && hk.ToJsonString() != ck.ToJsonString())
                yield return new Finding("item.kinematic", id.ToString(), name, hk.ToJsonString(), ck.ToJsonString());
        }
    }

    private static HashSet<int> KnownByHost(JsonObject host, JsonObject client)
    {
        string? clientName = client["me"]?["name"]?.GetValue<string>();
        JsonArray? players = host["interest"] as JsonArray;
        JsonNode? match = players?.FirstOrDefault(p => p?["name"]?.GetValue<string>() == clientName)
                          ?? (players?.Count == 1 ? players[0] : null);
        return match?["known"] is JsonArray ids ? ids.Select(i => i!.GetValue<int>()).ToHashSet() : new HashSet<int>();
    }

    private static IEnumerable<Finding> CompareCars(JsonObject host, JsonObject client)
    {
        Dictionary<int, JsonObject> h = ById(host["cars"]);
        Dictionary<int, JsonObject> c = ById(client["cars"]);

        foreach (var (id, car) in h.Where(p => !c.ContainsKey(p.Key)))
            yield return new Finding("car.missing-on-client", id.ToString(), S(car, "cid"), "present", "absent");
        foreach (var (id, car) in c.Where(p => !h.ContainsKey(p.Key)))
            yield return new Finding("car.ghost-on-client", id.ToString(), S(car, "cid"), "absent", "present");

        foreach (var (id, hc) in h)
        {
            if (!c.TryGetValue(id, out JsonObject? cc)) continue;
            string name = S(hc, "cid");
            if (S(hc, "der") != S(cc, "der")) yield return new Finding("car.derailed", id.ToString(), name, S(hc, "der"), S(cc, "der"));
            if (S(hc, "cf") != S(cc, "cf") || S(hc, "cr") != S(cc, "cr"))
                yield return new Finding("car.coupling", id.ToString(), name,
                    $"front {S(hc, "cf")} rear {S(hc, "cr")}", $"front {S(cc, "cf")} rear {S(cc, "cr")}");

            double speed = Math.Max(Math.Abs(F(hc, "spd")), Math.Abs(F(cc, "spd")));
            if (Vec(hc["pos"]) is { } a && Vec(cc["pos"]) is { } b)
            {
                double distance = Dist(a, b);
                if (distance > 0.5 + speed * 0.5)
                    yield return new Finding("car.position", id.ToString(), name, $"{Fmt(a)} @ {speed:0.#} m/s", $"{Fmt(b)} ({distance:0.##} m off)");
            }
            if (speed < 1 && TrackNames(hc) != TrackNames(cc))
                yield return new Finding("car.track", id.ToString(), name, TrackNames(hc), TrackNames(cc));
            if (Math.Abs(F(hc, "hb") - F(cc, "hb")) > 0.1)
                yield return new Finding("car.handbrake", id.ToString(), name, S(hc, "hb"), S(cc, "hb"));
            if (Math.Abs(F(hc, "bp") - F(cc, "bp")) > 0.3)
                yield return new Finding("car.brake-pipe", id.ToString(), name, S(hc, "bp"), S(cc, "bp"));
        }
    }

    private static IEnumerable<Finding> CompareWorld(JsonObject host, JsonObject client)
    {
        double hm = F(host["world"]!.AsObject(), "money"), cm = F(client["world"]!.AsObject(), "money");
        if (Math.Abs(hm - cm) > 1) yield return new Finding("world.money", "money", "money", hm.ToString("0.##"), cm.ToString("0.##"));

        Dictionary<int, int> hj = Junctions(host), cj = Junctions(client);
        foreach (var (id, branch) in hj)
            if (cj.TryGetValue(id, out int other) && other != branch)
                yield return new Finding("world.junction", id.ToString(), "junction " + id, "branch " + branch, "branch " + other);
    }

    private static Dictionary<int, int> Junctions(JsonObject snapshot) =>
        (snapshot["world"]?["junctions"] as JsonArray ?? new JsonArray())
        .Select(n => n!.GetValue<int>())
        .GroupBy(v => v / 10).ToDictionary(g => g.Key, g => g.First() % 10);

    // ---------------------------------------------------------------- reporting

    private static JsonObject Counts(JsonObject host, JsonObject client) => new()
    {
        ["hostItems"] = (host["items"] as JsonArray)?.Count,
        ["clientItems"] = (client["items"] as JsonArray)?.Count,
        ["knownToClientPerHost"] = KnownByHost(host, client).Count,
        ["hostCars"] = (host["cars"] as JsonArray)?.Count,
        ["clientCars"] = (client["cars"] as JsonArray)?.Count,
        ["playerDistanceMetres"] = Vec(host["player"]) is { } a && Vec(client["player"]) is { } b ? Math.Round(Dist(a, b), 1) : null
    };

    private static JsonObject CountByKind(IEnumerable<Tally> tallies)
    {
        JsonObject o = new();
        foreach (var group in tallies.GroupBy(t => t.First.Kind).OrderBy(g => Rank(g.Key)))
            o[group.Key] = group.Count();
        return o;
    }

    private static IEnumerable<Tally> Ordered(IEnumerable<Tally> tallies) =>
        tallies.OrderBy(t => Rank(t.First.Kind)).ThenByDescending(t => t.Count).ThenBy(t => t.First.Id);

    private static JsonObject Describe(Tally t) => new()
    {
        ["kind"] = t.Last.Kind,
        ["id"] = t.Last.Id,
        ["name"] = t.Last.Name,
        ["seen"] = t.Count,
        ["host"] = t.Last.Host,
        ["client"] = t.Last.Client
    };

    private static int Rank(string kind)
    {
        int i = Array.IndexOf(KindOrder, kind);
        return i < 0 ? KindOrder.Length : i;
    }

    // ---------------------------------------------------------------- json helpers

    private static Dictionary<int, JsonObject> ById(JsonNode? array)
    {
        Dictionary<int, JsonObject> map = new();
        if (array is JsonArray a)
            foreach (JsonNode? node in a)
                if (node is JsonObject o && o["id"] is JsonValue id)
                    map[id.GetValue<int>()] = o;
        return map;
    }

    private static string S(JsonObject o, string key) => o[key] switch
    {
        null => "null",
        JsonValue v when v.TryGetValue(out string? s) => s ?? "null",
        JsonNode n => n.ToJsonString()
    };

    private static double F(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out double d) ? d : 0;

    private static double[]? Vec(JsonNode? node) =>
        node is JsonArray a && a.Count == 3 ? a.Select(x => x!.GetValue<double>()).ToArray() : null;

    private static double Dist(double[] a, double[] b) =>
        Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

    private static string Fmt(double[] v) => $"({v[0]:0.##}, {v[1]:0.##}, {v[2]:0.##})";

    private static string Name(Dictionary<int, JsonObject> items, int id) =>
        items.TryGetValue(id, out JsonObject? o) ? S(o, "n") : "?";

    private static string Brief(JsonObject item) => $"{S(item, "s")} {S(item, "p")} active={S(item, "a")}";

    private static string TrackNames(JsonObject car) =>
        car["bogies"] is JsonArray a ? string.Join(",", a.Select(b => b?.GetValue<string>()?.Split('@')[0])) : "";
}
