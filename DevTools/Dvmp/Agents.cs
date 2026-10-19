using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dvmp;

internal sealed record AgentInfo(int Pid, int Port, string Token, string? AgentVersion, DateTime StartedUtc, string[]? Args);

internal sealed class Agent(AgentInfo info, RemoteInfo? remote = null)
{
    public AgentInfo Info { get; } = info;
    /// <summary>Set for a game on someone else's PC (see <see cref="Remotes"/>); its pid means nothing here.</summary>
    public RemoteInfo? Remote { get; } = remote;
    public JsonObject? Status { get; set; }
    public string? StatusError { get; set; }

    public int Pid => Info.Pid;
    public string Role => Status?["role"]?.GetValue<string>() ?? "unknown";
    /// <summary>The session the game was launched into (see <see cref="Sessions"/>).</summary>
    public string Session => Sessions.Of(Info.Args);
    public string Label => Remote != null ? $"{Role}@{Remotes.TargetPrefix}{Remote.Name}"
        : Session == Sessions.Default ? $"{Role}#{Pid}" : $"{Role}#{Pid}@{Session}";
}

internal sealed class AgentException(string message) : Exception(message);

/// <summary>Finds running game processes with the dev agent and talks to them.</summary>
internal static class Agents
{
    private static readonly JsonSerializerOptions fileJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>The current session's games, or every session's with <paramref name="all"/>.</summary>
    public static async Task<List<Agent>> DiscoverAsync(CancellationToken ct = default, bool all = false)
    {
        List<Agent> agents = new();
        if (!Directory.Exists(Paths.AgentsDir)) return agents;

        foreach (string file in Directory.GetFiles(Paths.AgentsDir, "agent-*.json"))
        {
            AgentInfo? info;
            try { info = JsonSerializer.Deserialize<AgentInfo>(await File.ReadAllTextAsync(file, ct), fileJson); }
            catch { continue; }
            if (info == null) continue;

            if (!IsAlive(info.Pid))
            {
                TryDelete(file); // the game crashed or was killed without cleaning up
                continue;
            }
            Agent agent = new(info);
            if (all || agent.Session == Sessions.Current)
                agents.Add(agent);
        }

        await Task.WhenAll(agents.Select(async agent =>
        {
            try { agent.Status = await CallAsync(agent, "/v1/status", new JsonObject(), TimeSpan.FromSeconds(5), ct); }
            catch (Exception e) { agent.StatusError = e.Message; }
        }));
        return agents.OrderBy(a => a.Role == "host" ? 0 : a.Role == "client" ? 1 : 2).ThenBy(a => a.Pid).ToList();
    }

    /// <summary>Resolves "host", "client", "all", a pid, or "remote:NAME" into concrete agents.</summary>
    public static async Task<List<Agent>> ResolveAsync(string? target, CancellationToken ct = default)
    {
        target = string.IsNullOrWhiteSpace(target) ? "all" : target.Trim().ToLowerInvariant();
        if (target.StartsWith(Remotes.TargetPrefix, StringComparison.Ordinal))
            return new List<Agent> { await RemoteAsync(target[Remotes.TargetPrefix.Length..], ct) };
        List<Agent> all = await DiscoverAsync(ct);
        if (all.Count == 0)
            throw new AgentException(Sessions.IsDefault
                ? "no game is running with the dev agent (start one with dvmp_up, or launch a Debug build)"
                : $"no game is running in session '{Sessions.Current}' (start one with dvmp up --session {Sessions.Current})");

        List<Agent> selected = target switch
        {
            "all" or "both" or "*" => all,
            "host" or "server" => all.Where(a => a.Role == "host").ToList(),
            "client" or "clients" => all.Where(a => a.Role == "client").ToList(),
            _ when int.TryParse(target, out int pid) => all.Where(a => a.Pid == pid).ToList(),
            _ => throw new AgentException($"unknown target '{target}' (use host, client, all, or a pid)")
        };
        if (selected.Count == 0)
            throw new AgentException($"no agent matches '{target}'. Running: " + string.Join(", ", all.Select(a => a.Label)));
        return selected;
    }

    /// <summary>A paired remote game, with its status (which also checks the session code).</summary>
    private static async Task<Agent> RemoteAsync(string name, CancellationToken ct)
    {
        RemoteInfo remote = Remotes.Load(name);
        Agent agent = new(new AgentInfo(0, remote.Port, string.Empty, null, remote.PairedUtc, null), remote);
        agent.Status = await CallAsync(agent, "/v1/status", new JsonObject(), TimeSpan.FromSeconds(10), ct);
        return agent;
    }

    public static async Task<JsonObject> CallAsync(Agent agent, string path, JsonObject body, TimeSpan timeout, CancellationToken ct = default)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        string host = agent.Remote?.Address ?? "127.0.0.1";
        using HttpRequestMessage request = new(HttpMethod.Post, $"http://{host}:{agent.Info.Port}{path}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (agent.Remote != null) Remotes.Authorize(request, agent.Remote);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agent.Info.Token);

        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AgentException($"{agent.Label}: no response within {timeout.TotalSeconds:0.#}s" +
                                     (agent.Remote != null ? "; " + Remotes.Hint(agent.Remote) : string.Empty));
        }
        catch (HttpRequestException e)
        {
            throw new AgentException($"{agent.Label}: {e.Message}" +
                                     (agent.Remote != null ? "; " + Remotes.Hint(agent.Remote) : string.Empty));
        }

        string text = await response.Content.ReadAsStringAsync(ct);
        JsonObject? json;
        try { json = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { json = null; }
        if (json == null) throw new AgentException($"{agent.Label}: HTTP {(int)response.StatusCode}: {Truncate(text, 400)}");
        if (!response.IsSuccessStatusCode && json["error"] is { } err)
            throw new AgentException($"{agent.Label}: {err}");
        return json;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { }
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
