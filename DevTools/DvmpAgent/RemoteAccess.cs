using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Dvmp.Agent;

internal enum RemoteMode { Off, Pairing, Session }

/// <summary>A debugger that completed pairing: its long-term secret, bound to its Tailscale address.</summary>
internal sealed class Pairing
{
    public string Id;
    public string Name;
    public string Peer;
    public string Secret;
    public DateTime CreatedUtc;
    public DateTime? LastUsedUtc;
}

/// <summary>A paired debugger making an authenticated request.</summary>
internal sealed class RemoteCaller
{
    public Pairing Pairing;
    public IPAddress Peer;
}

/// <summary>
/// Remote debugging over Tailscale: lets a trusted person run the dev harness against this
/// game from their PC. Running probes is arbitrary code execution, so access takes two steps:
///
/// 1. Pairing, once per debugger: the player presses "Pair" and reads out a single-use code
///    (80 bits, 10 minutes). The debugger presents it and receives a long-term secret, bound to
///    the Tailscale address it paired from.
/// 2. A session, every launch: remote debugging always starts off. The player presses "Allow"
///    and reads out a fresh session code; requests carry HMAC(secret, code), so neither the
///    pairing nor an old code works on its own.
///
/// The listener binds only to this PC's Tailscale address and accepts only Tailscale peers. It
/// turns itself off after five failed attempts, after an hour without requests, when the
/// pairing code expires, or when the game closes. Every authenticated request is logged, with
/// the source of each probe, to remote-audit.log in the mod folder.
/// </summary>
internal static class RemoteAccess
{
    public const int FirstPort = 7790;
    private const int PortCount = 10;
    private const int MaxFailures = 5;
    public static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(60);

    // Crockford base32: no I, L, O or U, so codes survive being read aloud.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string SessionTokenLabel = "dvmp-remote-session-v1";

    private static readonly object gate = new();
    private static AgentServer server;
    private static string codeKey;
    private static int failures;
    private static DateTime pairingExpiresUtc;
    private static DateTime lastActivityUtc;
    private static List<Pairing> pairings;
    private static readonly LinkedList<string> activity = new();

    public static RemoteMode Mode { get; private set; }
    /// <summary>The code to read out, grouped for reading (pairing or session, per <see cref="Mode"/>).</summary>
    public static string Code { get; private set; }
    /// <summary>Why remote debugging last stopped or could not start; shown in the settings.</summary>
    public static string Message { get; private set; }
    public static string ConnectedName { get; private set; }
    public static DateTime? LastRequestUtc { get; private set; }

    public static string Address
    {
        get { lock (gate) return server == null ? null : $"{server.Address}:{server.Port}"; }
    }

    public static TimeSpan PairingTimeLeft => Mode == RemoteMode.Pairing ? pairingExpiresUtc - DateTime.UtcNow : TimeSpan.Zero;

    public static IReadOnlyList<string> Activity
    {
        get { lock (gate) return activity.ToList(); }
    }

    public static IReadOnlyList<Pairing> Pairings
    {
        get { lock (gate) return LoadedPairings().ToList(); }
    }

    private static string PairingsPath => Path.Combine(AgentMain.Mod.Path, "remote-pairings.json");
    private static string AuditPath => Path.Combine(AgentMain.Mod.Path, "remote-audit.log");

    // ---------------------------------------------------------------- start / stop

    public static void StartPairing() => Start(RemoteMode.Pairing);

    public static void AllowSession() => Start(RemoteMode.Session);

    private static void Start(RemoteMode mode)
    {
        lock (gate)
        {
            if (mode == RemoteMode.Session && LoadedPairings().Count == 0)
            {
                Message = "Nobody is paired yet: pair a debugger first.";
                return;
            }
            if (server == null)
            {
                IPAddress address = FindTailscaleAddress(out string why);
                if (address == null)
                {
                    Message = why;
                    return;
                }
                AgentServer candidate = new(address, FirstPort, PortCount, remote: true);
                try { candidate.Start(); }
                catch (Exception e)
                {
                    Message = "Could not listen on " + address + ": " + e.Message;
                    return;
                }
                server = candidate;
            }

            Mode = mode;
            failures = 0;
            Code = NewCode(mode == RemoteMode.Pairing ? 16 : 8);
            codeKey = Normalize(Code);
            pairingExpiresUtc = DateTime.UtcNow + PairingWindow;
            lastActivityUtc = DateTime.UtcNow;
            ConnectedName = null;
            LastRequestUtc = null;
            Message = null;
            Note(mode == RemoteMode.Pairing
                ? $"waiting to pair on {server.Address}:{server.Port}"
                : $"allowed paired debuggers on {server.Address}:{server.Port}");
        }
    }

    public static void Stop(string reason)
    {
        lock (gate) StopLocked(reason);
    }

    private static void StopLocked(string reason)
    {
        if (server == null && Mode == RemoteMode.Off) return;
        try { server?.Stop(); } catch { }
        server = null;
        Mode = RemoteMode.Off;
        Code = null;
        codeKey = null;
        ConnectedName = null;
        Message = "Remote debugging is off: " + reason + ".";
        Note("off: " + reason);
    }

    /// <summary>Expires the pairing code and idle sessions; called every frame.</summary>
    public static void Tick()
    {
        if (Mode == RemoteMode.Off) return;
        lock (gate)
        {
            if (Mode == RemoteMode.Pairing && DateTime.UtcNow > pairingExpiresUtc)
                StopLocked("the pairing code expired");
            else if (Mode == RemoteMode.Session && DateTime.UtcNow - lastActivityUtc > IdleLimit)
                StopLocked($"unused for {IdleLimit.TotalMinutes:0} minutes");
        }
    }

    // ---------------------------------------------------------------- authentication

    /// <summary>Accepts the pairing code (Authorization header) or throws.</summary>
    public static void CheckPairingCode(string presented, IPAddress peer)
    {
        lock (gate)
        {
            if (Mode != RemoteMode.Pairing || DateTime.UtcNow > pairingExpiresUtc)
                throw Fail(peer, 403, "this game isn't waiting to pair");
            if (!AgentServer.FixedTimeEquals(Normalize(presented), codeKey))
                throw Fail(peer, 401, "wrong pairing code");
        }
    }

    /// <summary>Creates the pairing after <see cref="CheckPairingCode"/>, then waits for that debugger's session.</summary>
    public static string CompletePairing(string body, IPAddress peer)
    {
        JObject request = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
        string name = Sanitize(request.Value<string>("name"));

        lock (gate)
        {
            // Two requests can both pass the code check; only the first one pairs.
            if (Mode != RemoteMode.Pairing) throw new HttpError(409, "the pairing code was already used");

            byte[] secret = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
            Pairing pairing = new()
            {
                Id = NewCode(8).Replace("-", string.Empty).ToLowerInvariant(),
                Name = name,
                Peer = peer.ToString(),
                Secret = Convert.ToBase64String(secret),
                CreatedUtc = DateTime.UtcNow
            };
            LoadedPairings().Add(pairing);
            SavePairings();
            Note($"paired with {pairing.Name} ({pairing.Peer})");
            AppendAudit($"PAIRED {pairing.Name} from {pairing.Peer} (pairing {pairing.Id})");

            // Pairing alone grants nothing: a session needs the code shown next.
            Mode = RemoteMode.Session;
            failures = 0;
            Code = NewCode(8);
            codeKey = Normalize(Code);
            lastActivityUtc = DateTime.UtcNow;

            return new JObject
            {
                ["ok"] = true,
                ["pairingId"] = pairing.Id,
                ["secret"] = pairing.Secret,
                ["agentVersion"] = AgentMain.Version
            }.ToString(Formatting.None);
        }
    }

    /// <summary>Accepts a paired debugger's session token for the current session code, or throws.</summary>
    public static RemoteCaller CheckSession(string pairingId, string presented, IPAddress peer)
    {
        lock (gate)
        {
            if (Mode != RemoteMode.Session)
                throw Fail(peer, 403, Mode == RemoteMode.Pairing
                    ? "this game is waiting to pair, not for a session"
                    : "remote debugging isn't allowed right now");

            Pairing pairing = LoadedPairings().FirstOrDefault(p => p.Id == pairingId);
            if (pairing == null)
                throw Fail(peer, 401, "not paired with this game (they may have removed the pairing)");
            if (pairing.Peer != peer.ToString())
                throw Fail(peer, 403, "request is not from the device that paired");
            if (!AgentServer.FixedTimeEquals(presented, SessionToken(pairing.Secret, pairing.Id, codeKey)))
                throw Fail(peer, 401, "wrong or out-of-date session code (the game restarted, or remote debugging was turned off and on)");

            lastActivityUtc = DateTime.UtcNow;
            LastRequestUtc = lastActivityUtc;
            if (ConnectedName != pairing.Name)
            {
                ConnectedName = pairing.Name;
                Note($"{pairing.Name} connected");
            }
            pairing.LastUsedUtc = DateTime.UtcNow;
            return new RemoteCaller { Pairing = pairing, Peer = peer };
        }
    }

    public static string SessionToken(string secret, string pairingId, string normalizedCode)
    {
        using HMACSHA256 hmac = new(Convert.FromBase64String(secret));
        byte[] mac = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{SessionTokenLabel}|{pairingId}|{normalizedCode}"));
        return BitConverter.ToString(mac).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static HttpError Fail(IPAddress peer, int status, string message)
    {
        failures++;
        Note($"refused {peer}: {message}");
        AppendAudit($"REFUSED {peer}: {message}");
        if (failures >= MaxFailures)
            StopLocked($"{MaxFailures} failed attempts");
        return new HttpError(status, message);
    }

    public static void RemovePairing(string id)
    {
        lock (gate)
        {
            Pairing pairing = LoadedPairings().FirstOrDefault(p => p.Id == id);
            if (pairing == null) return;
            LoadedPairings().Remove(pairing);
            SavePairings();
            Note($"removed pairing with {pairing.Name}");
            AppendAudit($"UNPAIRED {pairing.Name} ({pairing.Peer})");
            if (ConnectedName == pairing.Name) ConnectedName = null;
        }
    }

    // ---------------------------------------------------------------- audit

    /// <summary>Records an authenticated request before it runs, including a probe's source.</summary>
    public static void Audit(RemoteCaller caller, string path, string body)
    {
        string who = $"{caller.Pairing.Name} ({caller.Peer})";
        if (path != "/v1/eval")
        {
            AppendAudit($"{who} {path}");
            if (path != "/v1/status") Note($"{caller.Pairing.Name}: {path.Replace("/v1/", string.Empty)}");
            return;
        }

        JObject request;
        try { request = JObject.Parse(body); }
        catch { request = new JObject(); }
        string type = request.Value<string>("type");
        string hash = "?";
        try
        {
            using SHA256 sha = SHA256.Create();
            hash = BitConverter.ToString(sha.ComputeHash(Convert.FromBase64String(request.Value<string>("assembly") ?? string.Empty)))
                .Replace("-", string.Empty).ToLowerInvariant();
        }
        catch { }

        StringBuilder entry = new();
        entry.Append($"{who} /v1/eval {type} assembly sha256 {hash}");
        if (request["args"] is JObject args && args.Count > 0) entry.Append(" args ").Append(args.ToString(Formatting.None));
        // The source is what the debugger says it compiled; the hash identifies what actually runs.
        string source = request.Value<string>("source");
        entry.AppendLine();
        entry.Append(source == null ? "    (no source sent)" : Indent(source));
        AppendAudit(entry.ToString());
        Note($"{caller.Pairing.Name}: ran code ({hash.Substring(0, Math.Min(8, hash.Length))})");
        Debug.Log($"[DvmpDevAgent] remote eval by {who}: {type}, sha256 {hash}");
    }

    private static void AppendAudit(string entry)
    {
        try
        {
            lock (activity)
                File.AppendAllText(AuditPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {entry}{Environment.NewLine}");
        }
        catch (Exception e) { Debug.LogWarning("[DvmpDevAgent] could not write remote-audit.log: " + e.Message); }
    }

    private static void Note(string line)
    {
        lock (activity)
        {
            activity.AddFirst($"{DateTime.Now:HH:mm:ss} {line}");
            while (activity.Count > 12) activity.RemoveLast();
        }
        Debug.Log("[DvmpDevAgent] remote: " + line);
    }

    private static string Indent(string text) =>
        string.Join(Environment.NewLine, text.Replace("\r\n", "\n").Split('\n').Select(l => "    " + l));

    // ---------------------------------------------------------------- Tailscale

    /// <summary>Tailscale's address range, 100.64.0.0/10 (IPv4 only).</summary>
    public static bool IsTailscaleAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        return b[0] == 100 && (b[1] & 0xC0) == 64;
    }

    /// <summary>
    /// This PC's address on the Tailscale adapter. Only the adapter named Tailscale counts: an
    /// ISP's carrier-grade NAT uses the same range.
    /// </summary>
    private static IPAddress FindTailscaleAddress(out string why)
    {
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                bool tailscale = (nic.Name ?? string.Empty).IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (nic.Description ?? string.Empty).IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!tailscale) continue;
                foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                    if (IsTailscaleAddress(unicast.Address))
                    {
                        why = null;
                        return unicast.Address;
                    }
            }
        }
        catch (Exception e)
        {
            why = "Could not read this PC's network adapters: " + e.Message;
            return null;
        }
        why = "Tailscale isn't connected on this PC (no Tailscale adapter with a 100.x address).";
        return null;
    }

    // ---------------------------------------------------------------- codes and pairings

    private static string NewCode(int length)
    {
        byte[] bytes = new byte[length];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        StringBuilder code = new();
        for (int i = 0; i < length; i++)
        {
            if (i > 0 && i % 4 == 0) code.Append('-');
            code.Append(Alphabet[bytes[i] & 31]); // 256 is a multiple of 32: uniform
        }
        return code.ToString();
    }

    /// <summary>Upper case without separators, reading O as 0 and I/L as 1.</summary>
    public static string Normalize(string code)
    {
        if (code == null) return null;
        StringBuilder s = new();
        foreach (char c in code.ToUpperInvariant())
        {
            if (c == '-' || char.IsWhiteSpace(c)) continue;
            s.Append(c switch { 'O' => '0', 'I' or 'L' => '1', _ => c });
        }
        return s.ToString();
    }

    private static string Sanitize(string name)
    {
        string clean = new((name ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').Take(32).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "debugger" : clean.Trim();
    }

    private static List<Pairing> LoadedPairings()
    {
        if (pairings != null) return pairings;
        try
        {
            pairings = File.Exists(PairingsPath)
                ? JsonConvert.DeserializeObject<List<Pairing>>(File.ReadAllText(PairingsPath)) ?? new List<Pairing>()
                : new List<Pairing>();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DvmpDevAgent] could not read remote-pairings.json, starting with no pairings: " + e.Message);
            pairings = new List<Pairing>();
        }
        return pairings;
    }

    private static void SavePairings()
    {
        string temp = PairingsPath + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(pairings, Formatting.Indented));
        if (File.Exists(PairingsPath)) File.Delete(PairingsPath);
        File.Move(temp, PairingsPath);
    }
}
