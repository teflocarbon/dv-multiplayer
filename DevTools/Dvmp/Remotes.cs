using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dvmp;

/// <summary>A game on someone else's PC, paired for remote debugging over Tailscale.</summary>
internal sealed record RemoteInfo(string Name, string Address, int Port, string PairingId, string Secret,
    DateTime PairedUtc, string? SessionCode = null);

/// <summary>
/// Paired remote games, one file each in %LOCALAPPDATA%\dvmp\remotes. Each holds the pairing's
/// long-term secret and the current session code; requests carry HMAC(secret, code). The agent's
/// side is DvmpAgent/RemoteAccess.cs.
///
/// Remote games are only reached by an explicit "remote:NAME" target, never by "all", "host",
/// "client" or a pid, so dvmp up/down and the regression scenarios can't touch them.
/// </summary>
internal static class Remotes
{
    public const string TargetPrefix = "remote:";
    public const int DefaultPort = 7790;
    private const string SessionTokenLabel = "dvmp-remote-session-v1";

    private static readonly JsonSerializerOptions fileJson = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string Dir => Path.Combine(Paths.DataRoot, "remotes");

    public static bool ValidName(string name) =>
        name.Length is > 0 and <= 32 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static RemoteInfo Load(string name)
    {
        if (!ValidName(name)) throw new AgentException($"bad remote name '{name}' (letters, digits, - and _)");
        string file = Path.Combine(Dir, name + ".json");
        if (!File.Exists(file))
            throw new AgentException($"no remote named '{name}' (pair first: dvmp remote pair {name} ADDRESS PAIRING-CODE)");
        return JsonSerializer.Deserialize<RemoteInfo>(File.ReadAllText(file), fileJson)
               ?? throw new AgentException($"remote '{name}': unreadable {file}");
    }

    public static List<RemoteInfo> All() => !Directory.Exists(Dir)
        ? new List<RemoteInfo>()
        : Directory.GetFiles(Dir, "*.json")
            .Select(f => { try { return JsonSerializer.Deserialize<RemoteInfo>(File.ReadAllText(f), fileJson); } catch { return null; } })
            .OfType<RemoteInfo>()
            .OrderBy(r => r.Name)
            .ToList();

    private static void Save(RemoteInfo remote)
    {
        Directory.CreateDirectory(Dir);
        string file = Path.Combine(Dir, remote.Name + ".json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(remote, fileJson));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    public static bool Forget(string name)
    {
        string file = Path.Combine(Dir, name + ".json");
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    /// <summary>"100.1.2.3" or "100.1.2.3:7791".</summary>
    public static (string Host, int Port) ParseAddress(string address)
    {
        string host = address.Trim();
        int port = DefaultPort;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out int p))
        {
            port = p;
            host = host[..colon];
        }
        if (!System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new AgentException($"'{address}' is not an IPv4 address (use their Tailscale IP, as shown in their game)");
        return (host, port);
    }

    // ---------------------------------------------------------------- pair / connect

    /// <summary>Presents a pairing code shown in their game; stores the secret it returns.</summary>
    public static async Task<JsonObject> PairAsync(string name, string address, string pairingCode, string? label)
    {
        if (!ValidName(name)) throw new AgentException($"bad remote name '{name}' (letters, digits, - and _)");
        var (host, port) = ParseAddress(address);
        JsonObject reply = await PostAsync(host, port, "/v1/pair", new JsonObject { ["name"] = label ?? Environment.UserName },
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Normalize(pairingCode)));
        string pairingId = reply["pairingId"]?.GetValue<string>() ?? throw new AgentException("pairing reply had no pairingId");
        string secret = reply["secret"]?.GetValue<string>() ?? throw new AgentException("pairing reply had no secret");
        Save(new RemoteInfo(name, host, port, pairingId, secret, DateTime.UtcNow));
        return new JsonObject
        {
            ["ok"] = true,
            ["remote"] = name,
            ["paired"] = $"{host}:{port}",
            ["next"] = $"their game now shows a session code: dvmp remote connect {name} CODE"
        };
    }

    /// <summary>Stores this launch's session code and checks it against their game.</summary>
    public static async Task<JsonObject> ConnectAsync(string name, string sessionCode)
    {
        RemoteInfo remote = Load(name) with { SessionCode = Normalize(sessionCode) };
        Agent agent = new(new AgentInfo(0, remote.Port, string.Empty, null, remote.PairedUtc, null), remote);
        JsonObject status = await Agents.CallAsync(agent, "/v1/status", new JsonObject(), TimeSpan.FromSeconds(10));
        Save(remote);
        status.Remove("ok");
        return new JsonObject { ["ok"] = true, ["remote"] = name, ["target"] = TargetPrefix + name, ["status"] = status };
    }

    // ---------------------------------------------------------------- requests

    public static void Authorize(HttpRequestMessage request, RemoteInfo remote)
    {
        if (remote.SessionCode == null)
            throw new AgentException($"remote:{remote.Name}: no session yet. They press \"Allow\" in the mod's settings (Ctrl+F10), " +
                                     $"then: dvmp remote connect {remote.Name} CODE");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionToken(remote));
        request.Headers.Add("X-Dvmp-Pairing", remote.PairingId);
    }

    public static string SessionToken(RemoteInfo remote)
    {
        using HMACSHA256 hmac = new(Convert.FromBase64String(remote.Secret));
        byte[] mac = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{SessionTokenLabel}|{remote.PairingId}|{remote.SessionCode}"));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>Upper case without separators, reading O as 0 and I/L as 1 (as the agent does).</summary>
    public static string Normalize(string code)
    {
        StringBuilder s = new();
        foreach (char c in code.ToUpperInvariant())
        {
            if (c == '-' || char.IsWhiteSpace(c)) continue;
            s.Append(c switch { 'O' => '0', 'I' or 'L' => '1', _ => c });
        }
        return s.ToString();
    }

    public static string Hint(RemoteInfo remote) =>
        $"is remote debugging on in their game (Ctrl+F10 > Multiplayer Dev Tools) and Tailscale connected on both PCs? ({remote.Address}:{remote.Port})";

    private static async Task<JsonObject> PostAsync(string host, int port, string path, JsonObject body, Action<HttpRequestMessage> authorize)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"http://{host}:{port}{path}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        authorize(request);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new AgentException($"{host}:{port}: {e.Message}. Is their game waiting to pair, and Tailscale connected on both PCs?");
        }
        string text = await response.Content.ReadAsStringAsync();
        JsonObject? json = null;
        try { json = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { }
        if (!response.IsSuccessStatusCode || json == null)
            throw new AgentException($"{host}:{port}: {json?["error"]?.ToString() ?? $"HTTP {(int)response.StatusCode}"}");
        return json;
    }
}
