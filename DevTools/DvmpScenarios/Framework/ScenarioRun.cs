using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Mono.Cecil;
using Newtonsoft.Json.Linq;

namespace Dvmp.Scenarios
{
    /// <summary>A scenario: options with their defaults, and the orchestration.</summary>
    public interface IScenario
    {
        Type OptionsType { get; }
        Task Run(Pair pair, object options);
    }

    /// <summary>Base class of every scenario. <typeparamref name="TOptions"/>'s public fields are its
    /// options, set from `--arg NAME=value` (gate.sh's variables): SPEED sets Speed, WAGON_BRAKE sets
    /// WagonBrake.</summary>
    public abstract class Scenario<TOptions> : IScenario where TOptions : class, new()
    {
        protected abstract Task Run(Pair pair, TOptions options);
        Type IScenario.OptionsType => typeof(TOptions);
        Task IScenario.Run(Pair pair, object options) => Run(pair, (TOptions)options);
    }

    /// <summary>One run of one scenario against one session's pair.</summary>
    internal sealed class ScenarioRun
    {
        public static ScenarioRun Current { get; private set; }

        private readonly string session, gameDir, dvmp;
        private readonly Dictionary<string, GameAgent> agents = new();
        private readonly byte[] assembly;
        private readonly string multiplayerMvid;

        public ScenarioRun(string session, string gameDir, string dvmp)
        {
            this.session = session;
            this.gameDir = gameDir;
            this.dvmp = dvmp;
            (assembly, AssemblyHash) = RenamedAssembly();
            string built = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Multiplayer.dll");
            using (ModuleDefinition m = ModuleDefinition.ReadModule(built)) multiplayerMvid = m.Mvid.ToString();
            Current = this;
        }

        public Output Out { get; } = new();
        private Pair pair;
        public Pair Pair => pair ??= new Pair(this);
        public string AssemblyHash { get; }
        public List<string> Failures { get; } = new();
        public Stack<Func<Task>> Cleanups { get; } = new();
        public List<(Peer Peer, string Id, Task Task)> Started { get; } = new();

        public GameAgent Agent(string role) => agents.TryGetValue(role, out GameAgent a) ? a
            : throw new HarnessException($"no {role} in session '{session}'");

        /// <summary>Finds the session's host and client (dvmp launched them, from this install).</summary>
        public async Task DiscoverAsync()
        {
            agents.Clear();
            foreach (GameAgent a in await GameAgent.DiscoverAsync(session, gameDir))
                if (a.Role is "host" or "client" && !agents.ContainsKey(a.Role))
                    agents[a.Role] = a;
            foreach (string role in new[] { "host", "client" })
                if (!agents.ContainsKey(role))
                    throw new HarnessException($"no {role} running in session '{session}' from {gameDir} (start the pair with dvmp up --session {session})");
        }

        /// <summary>The game's agent, with this build of the scenarios loaded: once per launch.
        /// Refused (a harness error) when the game runs another Multiplayer.dll than this was built
        /// against.</summary>
        public async Task<GameAgent> EnsureLoadedAsync(string role)
        {
            GameAgent agent = Agent(role);
            if (agent.LoadedHash == AssemblyHash) return agent;
            JObject request = new() { ["hash"] = AssemblyHash, ["expect"] = new JObject { ["Multiplayer"] = multiplayerMvid } };
            try { await agent.CallAsync("/v1/load", request, TimeSpan.FromSeconds(15)); }
            catch (AgentRefused e) when (e.Status == 404)
            {
                request["assembly"] = Convert.ToBase64String(assembly);
                await agent.CallAsync("/v1/load", request, TimeSpan.FromSeconds(30));
            }
            catch (AgentRefused e) { throw new HarnessException($"{role}: {e.Message}"); }
            agent.LoadedHash = AssemblyHash;
            return agent;
        }

        public async Task RestartAsync(string save)
        {
            bool dedicated = Agent("host").Dedicated;
            Out.Note($"restarting the pair{(save != null ? " on " + save : string.Empty)}");
            Stopwatch clock = Stopwatch.StartNew();
            await DvmpAsync("down");
            List<string> up = new() { "up" };
            if (save != null) up.AddRange(new[] { "--save", save });
            if (dedicated) up.Add("--dedicated");
            await DvmpAsync(up.ToArray());
            await DiscoverAsync();
            Out.Line("pair", "Restart", "ok", clock.Elapsed);
        }

        private async Task DvmpAsync(params string[] args)
        {
            ProcessStartInfo start = new(dvmp, string.Join(" ", args.Concat(new[] { "--session", session }).Select(Quote)))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using Process p = Process.Start(start) ?? throw new HarnessException("couldn't start " + dvmp);
            Task<string> stdout = p.StandardOutput.ReadToEndAsync(), stderr = p.StandardError.ReadToEndAsync();
            await Task.Run(() => p.WaitForExit());
            if (p.ExitCode != 0)
                throw new HarnessException($"dvmp {string.Join(" ", args)} failed ({p.ExitCode}): {(await stderr).Trim()} {Tail(await stdout)}");
        }

        /// <summary>Before a run: the host forgets cars an earlier run's spawn steps made.</summary>
        public Task ForgetSpawnedAsync() => Pair.Host.Quiet(() => Cars.TakeSpawned());

        /// <summary>After a run's cleanups, for the leak check: the cars its spawn steps made that
        /// each game still has. A run leaves none behind.</summary>
        public async Task<PeerResults<string[]>> SpawnedLeftAsync()
        {
            string[] ids = await Pair.Host.Quiet(() => Cars.TakeSpawned());
            if (ids.Length == 0) return new PeerResults<string[]>(ids, ids);
            return new PeerResults<string[]>(await Pair.Host.Quiet(() => Cars.Existing(ids)), await Pair.Client.Quiet(() => Cars.Existing(ids)));
        }

        /// <summary>Cancels steps the scenario started and didn't wait for.</summary>
        public async Task CancelStartedAsync()
        {
            foreach (var (peer, id, task) in Started.Where(s => !s.Task.IsCompleted))
            {
                try { await Agent(peer.Name).CallAsync("/v1/cancel", new JObject { ["call"] = id }, TimeSpan.FromSeconds(10)); }
                catch (Exception e) { Out.Note($"couldn't cancel a {peer.Name} step: {e.Message}"); }
            }
        }

        /// <summary>On a failure: both games' screenshots and their log lines since the run started.</summary>
        public async Task SaveArtifactsAsync(string dir, Dictionary<string, long> logStart)
        {
            Directory.CreateDirectory(dir);
            foreach (var (role, agent) in agents)
            {
                try
                {
                    JObject shot = await agent.CallAsync("/v1/screenshot", new JObject { ["maxWidth"] = 960 }, TimeSpan.FromSeconds(20));
                    File.WriteAllBytes(Path.Combine(dir, role + ".jpg"), Convert.FromBase64String(shot.Value<string>("base64")));
                    JObject logs = await agent.CallAsync("/v1/logs", new JObject { ["sinceSeq"] = logStart.TryGetValue(role, out long s) ? s : 0, ["minLevel"] = "log", ["max"] = 2000 }, TimeSpan.FromSeconds(10));
                    File.WriteAllText(Path.Combine(dir, role + "-log.json"), logs.ToString());
                }
                catch (Exception e) { Out.Note($"{role}: no artifacts: {e.Message}"); }
            }
            Out.Note("artifacts: " + dir);
        }

        public async Task<Dictionary<string, long>> LogCursorsAsync()
        {
            Dictionary<string, long> cursors = new();
            foreach (var (role, agent) in agents)
            {
                try { cursors[role] = (await agent.CallAsync("/v1/logs", new JObject { ["sinceSeq"] = long.MaxValue, ["max"] = 1 }, TimeSpan.FromSeconds(10))).Value<long>("lastSeq"); }
                catch (HarnessException) { }
            }
            return cursors;
        }

        // A unique name per build, as probes get one per compile: a game that stays up across
        // rebuilds then holds one copy of each build, never two assemblies with one name.
        private static (byte[], string) RenamedAssembly()
        {
            string path = typeof(ScenarioRun).Assembly.Location;
            byte[] original = File.ReadAllBytes(path);
            string id = Sha256(original).Substring(0, 12);
            using MemoryStream input = new(original), output = new();
            using (AssemblyDefinition a = AssemblyDefinition.ReadAssembly(input))
            {
                a.Name.Name = "DvmpScenarios_" + id;
                a.MainModule.Name = a.Name.Name + ".dll";
                a.Write(output);
            }
            byte[] renamed = output.ToArray();
            return (renamed, Sha256(renamed));
        }

        private static string Sha256(byte[] bytes)
        {
            using SHA256 sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        }

        private static string Quote(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;
        private static string Tail(string s) => s.Length > 400 ? s.Substring(s.Length - 400) : s;
    }

    /// <summary>The run's output: a line per step with when it ended and how long it took, the
    /// checks, and notes.</summary>
    internal sealed class Output
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly object gate = new();

        public void Line(string who, string what, string result, TimeSpan? took = null, IEnumerable<string> notes = null)
        {
            lock (gate)
            {
                Console.WriteLine($"[{T(clock.Elapsed)}] {who,-7} {what,-34} {result,-7}{(took is { } d ? T(d) : string.Empty)}");
                foreach (string n in notes ?? Enumerable.Empty<string>()) Console.WriteLine($"{"",19}{n}");
            }
        }

        public void Check(bool ok, string what, string detail)
        {
            lock (gate) Console.WriteLine($"[{T(clock.Elapsed)}] check   {(ok ? "ok  " : "FAIL")} {what}: {detail}");
        }

        public void Note(string note)
        {
            lock (gate) Console.WriteLine($"[{T(clock.Elapsed)}] note    {note}");
        }

        public void Text(string text)
        {
            lock (gate) Console.WriteLine(text);
        }

        public TimeSpan Elapsed => clock.Elapsed;

        private static string T(TimeSpan t) => t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(6) + "s";
    }
}
