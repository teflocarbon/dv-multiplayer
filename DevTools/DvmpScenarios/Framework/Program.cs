using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// DvmpScenarios.exe, run by `dvmp scenario ...`:
    ///   list                                   scenarios, their options and defaults
    ///   check                                  every scenario and step is well formed (no games)
    ///   run NAME [--arg K=V]... [--log-dir D]  one run against $DVMP_SESSION's pair
    /// Exit codes: 0 pass, 1 fail, 2 usage, 3 harness error (not a test result).
    /// </summary>
    internal static class Program
    {
        public const int Pass = 0, Fail = 1, Usage = 2, HarnessError = 3;

        public static int Main(string[] args)
        {
            // Before any game type is touched: the game's assemblies stay in its install.
            Resolver.Install(GameDir);
            return Run(args);
        }

        public static string GameDir => Environment.GetEnvironmentVariable("DVMP_DV_DIR") is { Length: > 0 } d ? d
            : @"C:\Program Files (x86)\Steam\steamapps\common\Derail Valley";

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            string command = args.FirstOrDefault();
            try
            {
                return command switch
                {
                    "list" => List(),
                    "check" => Check(),
                    "run" when args.Length > 1 => RunAsync(args[1], args).GetAwaiter().GetResult(),
                    _ => UsageError("usage: DvmpScenarios list | check | run NAME [--arg K=V]... [--session S] [--log-dir DIR] [--dvmp PATH]")
                };
            }
            catch (UsageException e) { return UsageError(e.Message); }
        }

        // Types the exe can load: in-game helper types built on game types it can't load here (a
        // MonoBehaviour from an assembly not in the install) are left out; nothing here needs them.
        private static IEnumerable<Type> Types()
        {
            try { return typeof(Program).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                if (Environment.GetEnvironmentVariable("DVMP_SCENARIOS_DEBUG") == "1")
                    foreach (Exception le in e.LoaderExceptions.Take(10)) Console.Error.WriteLine(le.Message);
                return e.Types.Where(t => t != null);
            }
        }

        private static IEnumerable<(ScenarioAttribute Info, Type Type)> Scenarios() =>
            Types()
                .Select(t => (Info: t.GetCustomAttribute<ScenarioAttribute>(), Type: t))
                .Where(s => s.Info != null)
                .OrderBy(s => s.Info.Name);

        private static int List()
        {
            foreach (var (info, type) in Scenarios())
            {
                object defaults = Activator.CreateInstance(((IScenario)Activator.CreateInstance(type)).OptionsType);
                string needs = type.GetCustomAttribute<NeedsAttribute>() is { } n ? "  [needs " + string.Join(", ", n.Needs) + "]" : string.Empty;
                Console.WriteLine($"{info.Name}{needs}\n    {info.Description}");
                foreach (FieldInfo f in defaults.GetType().GetFields())
                    Console.WriteLine($"    {Options.EnvName(f.Name)}={Options.Show(f.GetValue(defaults))}");
            }
            return Pass;
        }

        /// <summary>What the compiler can't see: every scenario has options, every step takes and
        /// returns plain data. Run by gate.sh before its first run.</summary>
        private static int Check()
        {
            List<string> problems = new();
            HashSet<string> names = new();
            foreach (var (info, type) in Scenarios())
            {
                if (!names.Add(info.Name)) problems.Add($"two scenarios are named {info.Name}");
                if (!typeof(IScenario).IsAssignableFrom(type)) { problems.Add($"{type.Name} isn't a Scenario<>"); continue; }
                try { Activator.CreateInstance(((IScenario)Activator.CreateInstance(type)).OptionsType); }
                catch (Exception e) { problems.Add($"{info.Name}: options: {e.Message}"); }
            }
            foreach (MethodInfo m in Types().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)))
            {
                if (m.GetCustomAttribute<StepAttribute>() == null) continue;
                try { StepCall.CheckSignature(m); }
                catch (ScenarioException e) { problems.Add(e.Message); }
            }
            foreach (string p in problems) Console.WriteLine("FAIL " + p);
            Console.WriteLine(problems.Count == 0 ? $"ok: {names.Count} scenario(s)" : $"{problems.Count} problem(s)");
            return problems.Count == 0 ? Pass : Fail;
        }

        private static async Task<int> RunAsync(string name, string[] args)
        {
            var found = Scenarios().FirstOrDefault(s => s.Info.Name == name);
            if (found.Type == null) throw new UsageException($"no scenario named {name} (DvmpScenarios list)");
            IScenario scenario = (IScenario)Activator.CreateInstance(found.Type);
            object options = Options.Bind(scenario.OptionsType, Values(args, "--arg"));
            string session = Value(args, "--session")
                ?? (Environment.GetEnvironmentVariable("DVMP_SESSION") is { Length: > 0 } s ? s : "default");
            string dvmp = Value(args, "--dvmp") ?? Environment.GetEnvironmentVariable("DVMP") ?? "dvmp";
            string logDir = Value(args, "--log-dir") ?? Path.Combine(Path.GetTempPath(), "dvmp-scenarios");
            int budget = found.Type.GetCustomAttribute<BudgetAttribute>()?.Seconds ?? 600;

            ScenarioRun run = new(session, GameDir, dvmp);
            Pair pair = new(run);
            run.Out.Text($"== {name}  session {session}  {Options.Describe(options)}");
            Dictionary<string, HashSet<string>> carsBefore = null;
            Dictionary<string, long> logStart = new();
            string error = null;
            bool harness = false;
            try
            {
                await run.DiscoverAsync();
                run.Out.Text($"   {(pair.Dedicated ? "dedicated host" : "player host")}; client #{run.Agent("client").Pid}, host #{run.Agent("host").Pid}");
                CheckNeeds(found.Type, pair);
                logStart = await run.LogCursorsAsync();
                carsBefore = await run.CarsAsync(pair);
                Task body = scenario.Run(pair, options);
                if (await Task.WhenAny(body, Task.Delay(TimeSpan.FromSeconds(budget))) != body)
                    throw new StepFailed($"the run took longer than its budget of {budget} s", null);
                await body;
            }
            catch (RequireFailed) { }
            catch (HarnessException e) { error = e.Message; harness = true; }
            catch (StepFailed e) { error = e.Message; }
            catch (ScenarioException e) { error = "scenario code: " + e.Message; }
            catch (Exception e) { error = e.ToString(); }

            if (error != null) run.Out.Text((harness ? "ERROR " : "FAIL ") + error);
            await CleanUpAsync(run, pair, carsBefore, harness);

            bool passed = error == null && run.Failures.Count == 0;
            if (!passed && !harness)
                await run.SaveArtifactsAsync(Path.Combine(logDir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}-{session}"), logStart);
            string summary = passed ? "PASS" : harness ? "ERROR" : "FAIL";
            run.Out.Text($"{summary}  {name}  {run.Out.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s" +
                         (run.Failures.Count > 0 ? "  " + string.Join("; ", run.Failures) : string.Empty));
            return passed ? Pass : harness ? HarnessError : Fail;
        }

        // Cleanups, then the leak check: every car the run spawned is gone again, on both games.
        private static async Task CleanUpAsync(ScenarioRun run, Pair pair, Dictionary<string, HashSet<string>> carsBefore, bool harness)
        {
            try { await run.CancelStartedAsync(); }
            catch (Exception e) { run.Out.Note("cancelling started steps: " + e.Message); }
            while (run.Cleanups.Count > 0)
            {
                try { await run.Cleanups.Pop()(); }
                catch (Exception e) { run.Out.Note("cleanup failed: " + e.Message); }
            }
            if (carsBefore == null || harness) return;
            try
            {
                foreach (var (peer, after) in (await run.CarsAsync(pair)).Select(kv => (kv.Key, kv.Value)))
                    Expect.Empty(after.Where(id => !carsBefore.TryGetValue(peer, out var before) || !before.Contains(id)), $"{peer}: cars the run left behind");
            }
            catch (Exception e) { run.Out.Note("leak check: " + e.Message); }
        }

        private static void CheckNeeds(Type type, Pair pair)
        {
            foreach (Need need in type.GetCustomAttribute<NeedsAttribute>()?.Needs ?? Array.Empty<Need>())
            {
                if (need == Need.PlayerHost && pair.Dedicated) throw new HarnessException("this scenario needs a player host; the session's host is dedicated");
                if (need == Need.DedicatedHost && !pair.Dedicated) throw new HarnessException("this scenario needs a dedicated host; the session's host is a player");
            }
        }

        private static string Value(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static IEnumerable<string> Values(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == name) yield return args[i + 1];
        }

        private static int UsageError(string message)
        {
            Console.Error.WriteLine(message);
            return Usage;
        }
    }

    internal sealed class UsageException(string message) : Exception(message);

    /// <summary>Scenario options from `--arg NAME=value`. A name the scenario doesn't have is an
    /// error, not ignored: FOO=1 is a typo.</summary>
    internal static class Options
    {
        public static object Bind(Type type, IEnumerable<string> pairs)
        {
            object options = Activator.CreateInstance(type);
            foreach (string pair in pairs)
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) throw new UsageException($"--arg {pair}: expected NAME=value");
                string key = pair.Substring(0, eq), value = pair.Substring(eq + 1);
                FieldInfo field = type.GetFields().FirstOrDefault(f => Normal(f.Name) == Normal(key))
                    ?? throw new UsageException($"unknown option {key} (options: {string.Join(", ", type.GetFields().Select(f => EnvName(f.Name)))})");
                try { field.SetValue(options, Parse(field.FieldType, value)); }
                catch (Exception e) when (e is FormatException or OverflowException)
                {
                    throw new UsageException($"{key}={value}: not a {field.FieldType.Name}");
                }
            }
            return options;
        }

        public static string Describe(object options) =>
            string.Join(" ", options.GetType().GetFields().Select(f => $"{EnvName(f.Name)}={Show(f.GetValue(options))}"));

        /// <summary>WagonBrake → WAGON_BRAKE.</summary>
        public static string EnvName(string field) =>
            string.Concat(field.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : char.ToUpperInvariant(c).ToString()));

        public static string Show(object v) => v switch
        {
            null => "",
            bool b => b ? "1" : "0",
            float f => f.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            string[] a => string.Join(",", a),
            _ => v.ToString()
        };

        private static string Normal(string name) => name.Replace("_", string.Empty).ToLowerInvariant();

        private static object Parse(Type t, string v)
        {
            if (t == typeof(string)) return v;
            if (t == typeof(bool)) return v is "1" or "true" or "yes" ? true : v is "0" or "false" or "no" or "" ? false : throw new FormatException();
            if (t == typeof(string[])) return v.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (t == typeof(float[])) return v.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            return Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Finds the game's assemblies (and the mod's dependencies) in the install dvmp points
    /// at, so the exe can read game types without copies of them.</summary>
    internal static class Resolver
    {
        public static void Install(string gameDir)
        {
            string[] dirs =
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.Combine(gameDir, "DerailValley_Data", "Managed"),
                Path.Combine(gameDir, "DerailValley_Data", "Managed", "UnityModManager"),
                Path.Combine(gameDir, "Mods", "Multiplayer"),
                Path.Combine(gameDir, "Mods", "MultiplayerDevTools")
            };
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                string file = new AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in dirs)
                {
                    string path = Path.Combine(dir, file);
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
        }
    }
}
