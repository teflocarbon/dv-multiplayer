using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dvmp;

/// <summary>
/// Named sessions: independent host + client pairs that can run side by side (see
/// docs/design/parallel-test-sessions.md). The session comes from --session, else DVMP_SESSION,
/// else "default", which behaves exactly as dvmp did before sessions existed. A game belongs to
/// the session named by its -dvmp-session launch argument (none: default).
/// </summary>
internal static class Sessions
{
    public const string Default = "default";
    private const int FirstPort = 7801;

    private static readonly AsyncLocal<string?> scoped = new();
    private static string? process;

    /// <summary>The session this command (or MCP call) acts on.</summary>
    public static string Current => scoped.Value ?? process ?? FromEnvironment;

    public static bool IsDefault => Current == Default;

    private static string FromEnvironment =>
        Environment.GetEnvironmentVariable("DVMP_SESSION") is { } s && Valid(s) ? s : Default;

    /// <summary>For the CLI: the session for the whole process.</summary>
    public static void SetForProcess(string name)
    {
        if (!Valid(name)) throw new AgentException($"invalid session name '{name}' (letters, digits, - and _, up to 16)");
        process = name;
    }

    /// <summary>For MCP calls: the session for the calling async flow only.</summary>
    public static IDisposable Use(string? name)
    {
        string? previous = scoped.Value;
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (!Valid(name)) throw new AgentException($"invalid session name '{name}' (letters, digits, - and _, up to 16)");
            scoped.Value = name;
        }
        return new Restore(() => scoped.Value = previous);
    }

    public static bool Valid(string? name) =>
        name is { Length: > 0 and <= 16 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The session a game was launched into, from its command line.</summary>
    public static string Of(string[]? args)
    {
        if (args == null) return Default;
        int i = Array.IndexOf(args, "-dvmp-session");
        return i >= 0 && i + 1 < args.Length && Valid(args[i + 1]) ? args[i + 1] : Default;
    }

    public static string Dir(string session) => Path.Combine(Paths.DataRoot, "sessions", session);

    /// <summary>Game logs: logs/ for the default session (as before), sessions/NAME/logs otherwise.</summary>
    public static string LogsDir => IsDefault ? Path.Combine(Paths.DataRoot, "logs") : Path.Combine(Dir(Current), "logs");

    /// <summary>A suffix for report file names and window titles; empty for the default session.</summary>
    public static string Suffix => IsDefault ? string.Empty : "-" + Current;

    /// <summary>
    /// The server port of a named session, allocated once and kept in its session.json. Two hosts
    /// on one port is not an error you see: the Steam transport reports "started" if its relay
    /// socket opens even when the UDP bind failed, and the second client then joins the first
    /// host. The default session uses the mod's own port setting (null here).
    /// </summary>
    public static int? Port()
    {
        if (IsDefault) return null;
        string file = Path.Combine(Dir(Current), "session.json");
        if (File.Exists(file))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file))?["port"]?.GetValue<int>() is int saved) return saved;
            }
            catch (JsonException) { }
        }
        using (LaunchLock())
        {
            HashSet<int> taken = AllocatedPorts();
            int port = FirstPort;
            while (taken.Contains(port) || Win32.UdpPortOwners(port).Count > 0) port++;
            Directory.CreateDirectory(Dir(Current));
            File.WriteAllText(file, new JsonObject { ["port"] = port, ["created"] = DateTime.UtcNow.ToString("o") }.ToJsonString());
            return port;
        }
    }

    private static HashSet<int> AllocatedPorts()
    {
        HashSet<int> ports = new();
        string root = Path.Combine(Paths.DataRoot, "sessions");
        if (!Directory.Exists(root)) return ports;
        foreach (string file in Directory.GetFiles(root, "session.json", SearchOption.AllDirectories))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file))?["port"]?.GetValue<int>() is int port) ports.Add(port);
            }
            catch { }
        }
        return ports;
    }

    /// <summary>
    /// Held while a game starts loading mods: Unity Mod Manager touches shared cache files (and
    /// the mod writes its Settings.xml) at load, so launches across sessions are staggered. A file
    /// opened without sharing, not a named mutex: those belong to a thread, and this is async.
    /// </summary>
    public static IDisposable LaunchLock(TimeSpan? timeout = null)
    {
        Directory.CreateDirectory(Paths.DataRoot);
        string path = Path.Combine(Paths.DataRoot, "launch.lock");
        DateTime until = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(250);
            }
        }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
