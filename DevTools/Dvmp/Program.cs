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
          dvmp status
          dvmp up [--fresh] [--save NAME|UID] [--visible] [--fps N] [--sound]
                                             start/reuse a joined host + client (minimised, muted)
          dvmp down                                  quit all agent-enabled games
          dvmp desync [-n samples] [-i seconds] [--top N]   cross-peer desync report
          dvmp soak [-t client|host|all] [-n actions] [--seed S] [-r radius] [--pause s] [--samples n]
          dvmp eval [-t target] [-m mode] [-u usings] (CODE | -f FILE)
          dvmp screenshot [-t target] [-w maxWidth] [-o DIR]
          dvmp logs [-t target] [-l minLevel] [-c contains] [-n max]

        target: host | client | all (default) | pid
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] == "mcp") return await RunMcpAsync();

        try
        {
            return args[0] switch
            {
                "status" => Print(await Operations.StatusAsync()),
                "up" => PrintResult(await Session.UpAsync(
                    new UpOptions(args.Contains("--fresh"), Opt(args, "--save"), 960, 540, Arrange: true,
                        Background: !args.Contains("--visible"), FpsCap: int.Parse(Opt(args, "--fps") ?? "30"),
                        Sound: args.Contains("--sound")),
                    Timeout.InfiniteTimeSpan, CancellationToken.None)),
                "down" => Print(await Session.DownAsync(CancellationToken.None)),
                "soak" => Print(await Soak.RunAsync(Opt(args, "-t"), int.Parse(Opt(args, "-n") ?? "15"), int.Parse(Opt(args, "--seed") ?? "1"),
                    double.Parse(Opt(args, "-r") ?? "40", System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(Opt(args, "--pause") ?? "1", System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(Opt(args, "--samples") ?? "6"), CancellationToken.None)),
                "desync" => Print(await Desync.RunAsync(int.Parse(Opt(args, "-n") ?? "6"),
                    double.Parse(Opt(args, "-i") ?? "1", System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(Opt(args, "--top") ?? "40"), CancellationToken.None)),
                "eval" => await EvalAsync(args),
                "screenshot" => await ScreenshotAsync(args),
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
        JsonObject result = await Operations.EvalAsync(code, Opt(args, "-t"), Opt(args, "-m"), Opt(args, "-u"), null,
            int.Parse(Opt(args, "--timeout") ?? "30"));
        Print(result);
        return result["ok"]?.GetValue<bool>() == false || HasFailure(result) ? 2 : 0;
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
