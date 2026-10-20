using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Dvmp.Scenarios
{
    /// <summary>The run's host and client. Steps run on one (<see cref="Host"/>, <see cref="Client"/>)
    /// or both at once (<see cref="Both"/>).</summary>
    public sealed class Pair
    {
        private readonly ScenarioRun run;

        internal Pair(ScenarioRun run)
        {
            this.run = run;
            Host = new Peer(run, "host");
            Client = new Peer(run, "client");
            Both = new PeerSet(Host, Client);
        }

        public Peer Host { get; }
        public Peer Client { get; }
        public PeerSet Both { get; }

        /// <summary>Whether the host is a dedicated host: no player, no avatar, no slot.</summary>
        public bool Dedicated => run.Agent("host").Dedicated;

        /// <summary>Runs after the scenario, pass or fail, last registered first.</summary>
        public void Cleanup(Func<Task> cleanup) => run.Cleanups.Push(cleanup);

        /// <summary>Quits both games and starts the pair again (as `dvmp down` and `dvmp up`), on
        /// the given save or the session's default; the scenario assembly is loaded again on the next
        /// step. Needs [Needs(Need.RestartsGames)].</summary>
        public Task Restart(string save = null) => run.RestartAsync(save);
    }

    public sealed class Peer
    {
        private readonly ScenarioRun run;

        internal Peer(ScenarioRun run, string name)
        {
            this.run = run;
            Name = name;
        }

        /// <summary>"host" or "client".</summary>
        public string Name { get; }

        public Task Run(Expression<Action> call, int timeoutSeconds = 60) => Invoke<object>(StepCall.From(call), timeoutSeconds, null);
        /// <summary>The framework's own steps (the leak check): not printed unless they fail.</summary>
        internal Task<T> Quiet<T>(Expression<Func<T>> call) => Invoke<T>(StepCall.From(call), 60, null, quiet: true);
        public Task Run(Expression<Func<IEnumerator>> call, int timeoutSeconds = 60) => Invoke<object>(StepCall.From(call), timeoutSeconds, null);
        public Task<T> Run<T>(Expression<Func<T>> call, int timeoutSeconds = 60) => Invoke<T>(StepCall.From(call), timeoutSeconds, null);
        public Task<T> Run<T>(Expression<Func<Step<T>, IEnumerator>> call, int timeoutSeconds = 60) => Invoke<T>(StepCall.From(call), timeoutSeconds, null);

        /// <summary>Starts a step and returns at once; await the result later. A step still running
        /// when the scenario ends is cancelled.</summary>
        public Running<object> Start(Expression<Func<IEnumerator>> call, int timeoutSeconds = 60) => StartStep<object>(StepCall.From(call), timeoutSeconds);
        public Running<T> Start<T>(Expression<Func<Step<T>, IEnumerator>> call, int timeoutSeconds = 60) => StartStep<T>(StepCall.From(call), timeoutSeconds);

        /// <summary>Delays every packet this game receives by <paramref name="ms"/> (Steam's fake
        /// lag) until the returned scope is disposed. 0 does nothing.</summary>
        public async Task<IDisposable> Lag(int ms)
        {
            if (ms <= 0) return new Scope(null);
            await Run(() => Net.SetReceiveLag(ms));
            Func<Task> undo = () => Run(() => Net.SetReceiveLag(0));
            run.Cleanups.Push(undo);   // also undone if the scenario fails inside the scope
            return new Scope(() => undo().GetAwaiter().GetResult());
        }

        /// <summary>Where this game's log is now: pass it to <see cref="LogsSince"/> later.</summary>
        public async Task<long> LogCursor() =>
            (await run.Agent(Name).CallAsync("/v1/logs", new JObject { ["sinceSeq"] = long.MaxValue, ["max"] = 1 }, TimeSpan.FromSeconds(10))).Value<long>("lastSeq");

        /// <summary>This game's log lines since <paramref name="cursor"/> at <paramref name="minLevel"/>
        /// (log, warning, error, exception) or worse.</summary>
        public async Task<LogLine[]> LogsSince(long cursor, string minLevel = "log")
        {
            JObject r = await run.Agent(Name).CallAsync("/v1/logs", new JObject { ["sinceSeq"] = cursor, ["minLevel"] = minLevel, ["max"] = 2000 }, TimeSpan.FromSeconds(10));
            return r["entries"]?.ToObject<LogLine[]>() ?? Array.Empty<LogLine>();
        }

        private Running<T> StartStep<T>(StepCall call, int timeoutSeconds)
        {
            string id = Guid.NewGuid().ToString("N");
            Task<T> task = Invoke<T>(call, timeoutSeconds, id);
            run.Started.Add((this, id, task));
            return new Running<T>(task);
        }

        internal async Task<T> Invoke<T>(StepCall call, int timeoutSeconds, string startedId, bool quiet = false)
        {
            GameAgent agent = await run.EnsureLoadedAsync(Name);
            Stopwatch clock = Stopwatch.StartNew();
            if (startedId != null) run.Out.Line(Name, call.Label, "started");
            JObject body = new()
            {
                ["hash"] = run.AssemblyHash,
                ["call"] = startedId ?? Guid.NewGuid().ToString("N"),
                ["type"] = call.Method.DeclaringType.FullName,
                ["method"] = call.Method.Name,
                ["parameters"] = new JArray(call.Parameters),
                ["args"] = JArray.FromObject(call.Args),
                ["timeoutMs"] = timeoutSeconds * 1000
            };
            JObject response;
            try { response = await agent.CallAsync("/v1/invoke", body, TimeSpan.FromSeconds(timeoutSeconds + 15)); }
            catch (AgentRefused e) { throw new HarnessException($"{Name}: {call.Label}: {e.Message}"); }
            IEnumerable<string> notes = response["notes"]?.Values<string>() ?? Enumerable.Empty<string>();
            if (response.Value<bool?>("ok") != true)
            {
                run.Out.Line(Name, call.Label, "FAIL", clock.Elapsed, notes);
                throw new StepFailed($"{Name}: {call.Label} failed: {response.Value<string>("error")}", response.Value<string>("stack"));
            }
            if (!quiet) run.Out.Line(Name, call.Label, "ok", clock.Elapsed, notes);
            JToken result = response["result"];
            return call.ResultType == null || result == null || result.Type == JTokenType.Null ? default : (T)result.ToObject(call.ResultType);
        }

        private sealed class Scope(Action dispose) : IDisposable
        {
            private Action dispose = dispose;
            public void Dispose()
            {
                dispose?.Invoke();
                dispose = null;
            }
        }
    }

    /// <summary>Both games: each step runs on the host and the client at once.</summary>
    public sealed class PeerSet
    {
        private readonly Peer host, client;

        internal PeerSet(Peer host, Peer client)
        {
            this.host = host;
            this.client = client;
        }

        public async Task<PeerResults<T>> Run<T>(Expression<Func<T>> call, int timeoutSeconds = 60)
        {
            StepCall step = StepCall.From(call);
            T[] r = await Task.WhenAll(host.Invoke<T>(step, timeoutSeconds, null), client.Invoke<T>(step, timeoutSeconds, null));
            return new PeerResults<T>(r[0], r[1]);
        }

        public async Task<PeerResults<T>> Run<T>(Expression<Func<Step<T>, IEnumerator>> call, int timeoutSeconds = 60) =>
            await Start(call, timeoutSeconds);

        public Running<PeerResults<T>> Start<T>(Expression<Func<Step<T>, IEnumerator>> call, int timeoutSeconds = 60)
        {
            Running<T> h = host.Start(call, timeoutSeconds), c = client.Start(call, timeoutSeconds);
            return new Running<PeerResults<T>>(Both(h.Task, c.Task));

            static async Task<PeerResults<T>> Both(Task<T> h, Task<T> c)
            {
                T[] r = await Task.WhenAll(h, c);
                return new PeerResults<T>(r[0], r[1]);
            }
        }
    }

    /// <summary>A step's result from each game, as <c>foreach (var (peer, value) in results)</c>.</summary>
    public sealed class PeerResults<T>(T host, T client) : IEnumerable<(string Peer, T Value)>
    {
        public T Host { get; } = host;
        public T Client { get; } = client;

        public IEnumerator<(string Peer, T Value)> GetEnumerator()
        {
            yield return ("host", Host);
            yield return ("client", Client);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A step started with Start: await it for its result.</summary>
    public sealed class Running<T>(Task<T> task)
    {
        public Task<T> Task { get; } = task;
        public TaskAwaiter<T> GetAwaiter() => Task.GetAwaiter();
    }

    /// <summary>A line of a game's log (DvmpAgent/LogBuffer.cs).</summary>
    public sealed class LogLine
    {
        public long Seq;
        public string Level, Message, Stack;
    }

    /// <summary>A step threw inside the game, or didn't finish in time.</summary>
    public sealed class StepFailed(string message, string gameStack) : Exception(message)
    {
        public string GameStack { get; } = gameStack;
    }
}
