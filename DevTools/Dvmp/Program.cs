using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dvmp;

internal static class Program
{
    private const string Usage = """
        dvmp - DVMP dev harness

          dvmp mcp                                   run as an MCP server over stdio
          dvmp status [--all]                        this session's games, or every session's
          dvmp up [--fresh] [--save NAME|UID] [--visible] [--fps N] [--bg-fps N] [--sound] [--trace] [--new career|sandbox [--name N]]
                                             start/reuse a joined host + client (minimised, muted)
          dvmp up --host-only --session NAME [--save ...]   start/reuse only a host, with its own identity,
                                             for a dvmp client (a later `up`) or your own game to join
          dvmp up ... --dedicated            launch the host with -mp-dedicated (a dedicated host: no avatar, no slot)
          dvmp up ... --fresh --verified     launch the host checking Steam tickets (no -mp-allow-unverified)
          dvmp down [--all]                          quit this session's games (--all: every session's)
          dvmp trace [-t target] [--ids 1,2] [--last N]   install/refresh tracer, show item history
          dvmp desync [-n samples] [-i seconds] [--top N]   cross-peer desync report
          dvmp soak [-t client|host|all] [-n actions] [--seed S] [-r radius] [--pause s] [--samples n] [--filter NamePrefix]
          dvmp eval [-t target] [-m mode] [-u usings] [-a key=value ...] (CODE | -f FILE)
          dvmp screenshot [-t target] [-w maxWidth] [-o DIR]
          dvmp logs [-t target] [-l minLevel] [-c contains] [-n max]
          dvmp perf [-t target] [-s seconds]         frame times, memory, physics, players and packets per type
          dvmp remote pair NAME ADDRESS[:PORT] PAIRING-CODE [--as LABEL]   pair with a game on someone else's PC (Tailscale)
          dvmp remote connect NAME SESSION-CODE      start a session with it (a new code every launch of their game)
          dvmp remote list | forget NAME             paired games; forget drops the pairing on this PC only

        target: host | client | all (default) | pid | remote:NAME (a paired remote game; never part of "all")
        --session NAME (or DVMP_SESSION): act on that host + client pair; several can run at once.
        Without it, the default session (as before sessions existed).
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        int sessionAt = Array.IndexOf(args, "--session");
        if (sessionAt >= 0)
        {
            if (sessionAt + 1 >= args.Length || !Sessions.Valid(args[sessionAt + 1]))
                return Fail("--session needs a name (letters, digits, - and _, up to 16)");
            Sessions.SetForProcess(args[sessionAt + 1]);
            args = args.Where((_, i) => i != sessionAt && i != sessionAt + 1).ToArray();
        }

        if (args[0] == "mcp") return await RunMcpAsync();

        try
        {
            return args[0] switch
            {
                "status" => Print(await Operations.StatusAsync(all: args.Contains("--all"))),
                "up" => PrintResult(await Session.UpAsync(
                    new UpOptions(args.Contains("--fresh"), Opt(args, "--save"), 960, 540, Arrange: true,
                        Background: !args.Contains("--visible"), FpsCap: int.Parse(Opt(args, "--fps") ?? "30"),
                        Sound: args.Contains("--sound"), Trace: args.Contains("--trace"),
                        NewSession: Opt(args, "--new"), NewSessionName: Opt(args, "--name"),
                        BackgroundFpsCap: Opt(args, "--bg-fps") is string bg ? int.Parse(bg) : null,
                        HostOnly: args.Contains("--host-only"), Dedicated: args.Contains("--dedicated"),
                        Verified: args.Contains("--verified")),
                    Timeout.InfiniteTimeSpan, CancellationToken.None)),
                "down" => Print(await Session.DownAsync(CancellationToken.None, all: args.Contains("--all"))),
                "soak" => Print(await Soak.RunAsync(Opt(args, "-t"), int.Parse(Opt(args, "-n") ?? "15"), int.Parse(Opt(args, "--seed") ?? "1"),
                    double.Parse(Opt(args, "-r") ?? "40", System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(Opt(args, "--pause") ?? "1", System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(Opt(args, "--samples") ?? "6"), CancellationToken.None, Opt(args, "--filter"))),
                "trace" => Print(await Tracer.RunAsync(Opt(args, "-t"), Opt(args, "--ids"), int.Parse(Opt(args, "--last") ?? "20"), CancellationToken.None)),
                "desync" => Print(await Desync.RunAsync(int.Parse(Opt(args, "-n") ?? "6"),
                    double.Parse(Opt(args, "-i") ?? "1", System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(Opt(args, "--top") ?? "40"), CancellationToken.None)),
                "eval" => await EvalAsync(args),
                "screenshot" => await ScreenshotAsync(args),
                "perf" => await PerfAsync(args),
                "remote" => await RemoteAsync(args),
                "logs" => Print(await Operations.LogsAsync(Opt(args, "-t"), Opt(args, "-l") ?? "warning",
                    Opt(args, "-c"), int.Parse(Opt(args, "-n") ?? "100"), sinceLast: false)),
                _ => Fail("unknown command: " + args[0] + "\n\n" + Usage)
            };
        }
        catch (AgentException e) { return Fail(e.Message); }
    }

    private static async Task<int> RunMcpAsync()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        // stdout carries the MCP protocol; every log line must go to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new() { Name = "dvmp", Version = "0.1.0" })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
        await builder.Build().RunAsync();
        return 0;
    }

    private static async Task<int> EvalAsync(string[] args)
    {
        string? file = Opt(args, "-f");
        string? code = file != null ? await File.ReadAllTextAsync(file) : Positional(args, 1);
        if (string.IsNullOrWhiteSpace(code)) return Fail("eval needs CODE or -f FILE");
        JsonObject result = await Operations.EvalAsync(code, Opt(args, "-t"), Opt(args, "-m"), Opt(args, "-u"), ProbeArgs(args),
            int.Parse(Opt(args, "--timeout") ?? "30"));
        Print(result);
        return result["ok"]?.GetValue<bool>() == false || HasFailure(result) ? 2 : 0;
    }

    private static async Task<int> PerfAsync(string[] args)
    {
        int seconds = int.Parse(Opt(args, "-s") ?? "10");
        JsonObject result = await Operations.EvalAsync(Session.LoadProbe("perf"), Opt(args, "-t"), "class", null,
            new Dictionary<string, string> { ["seconds"] = seconds.ToString() }, seconds + 30);
        Print(result);
        return result["ok"]?.GetValue<bool>() == false || HasFailure(result) ? 2 : 0;
    }

    private static async Task<int> RemoteAsync(string[] args)
    {
        string? verb = Positional(args, 1);
        string? name = Positional(args, 2);
        switch (verb)
        {
            case "pair" when name != null && Positional(args, 3) is string address && Positional(args, 4) is string code:
                return PrintResult(await Remotes.PairAsync(name, address, code, Opt(args, "--as")));
            case "connect" when name != null && Positional(args, 3) is string code:
                return PrintResult(await Remotes.ConnectAsync(name, code));
            case "list":
                JsonArray list = new();
                foreach (RemoteInfo r in Remotes.All())
                    list.Add(new JsonObject
                    {
                        ["target"] = Remotes.TargetPrefix + r.Name,
                        ["address"] = $"{r.Address}:{r.Port}",
                        ["pairedUtc"] = r.PairedUtc,
                        ["hasSessionCode"] = r.SessionCode != null
                    });
                return Print(new JsonObject { ["remotes"] = list });
            case "forget" when name != null:
                return Remotes.Forget(name) ? Print(new JsonObject { ["ok"] = true, ["forgot"] = name,
                    ["note"] = "the pairing still exists in their game until they remove it there" }) : Fail($"no remote named '{name}'");
            default:
                return Fail("usage: dvmp remote pair NAME ADDRESS[:PORT] PAIRING-CODE [--as LABEL] | connect NAME SESSION-CODE | list | forget NAME");
        }
    }

    private static async Task<int> ScreenshotAsync(string[] args)
    {
        string dir = Opt(args, "-o") ?? Path.Combine(Path.GetTempPath(), "dvmp-shots");
        Directory.CreateDirectory(dir);
        int failures = 0;
        foreach (var (agent, shot, error) in await Operations.ScreenshotAsync(Opt(args, "-t"), int.Parse(Opt(args, "-w") ?? "1280")))
        {
            if (shot == null) { Console.Error.WriteLine($"{agent.Label}: {error}"); failures++; continue; }
            string path = Path.Combine(dir, $"{agent.Role}-{agent.Pid}-{DateTime.Now:HHmmss}.jpg");
            await File.WriteAllBytesAsync(path, shot.Jpeg);
            Console.WriteLine(path);
        }
        return failures == 0 ? 0 : 2;
    }

    // Repeated "-a key=value" options become probe arguments (ctx.Args).
    private static Dictionary<string, string> ProbeArgs(string[] args)
    {
        Dictionary<string, string> result = new();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "-a" && args[i + 1].IndexOf('=') is int eq and > 0)
                result[args[i + 1][..eq]] = args[i + 1][(eq + 1)..];
        return result;
    }

    private static bool HasFailure(JsonObject result) =>
        result["results"] is JsonObject r && r.Any(p => p.Value?["ok"]?.GetValue<bool>() == false);

    private static int PrintResult(JsonObject node)
    {
        Print(node);
        return node["ok"]?.GetValue<bool>() == true ? 0 : 2;
    }

    private static int Print(JsonNode node)
    {
        Console.WriteLine(McpTools.Render(node));
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // The first argument after the command that is neither an option nor an option's value.
    private static string? Positional(string[] args, int start)
    {
        for (int i = start; i < args.Length; i++)
        {
            if (args[i].StartsWith('-') && args[i].Length > 1 && !args[i].Contains(' ')) { i++; continue; }
            return args[i];
        }
        return null;
    }
}
