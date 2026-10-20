using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Dvmp.Agent;

/// <summary>
/// Runs one probe (/v1/eval) or scenario step (/v1/invoke) on the main thread as a tracked root
/// in <see cref="ProbeRegistry"/>: every coroutine it starts is stopped when it returns, fails,
/// times out or is cancelled, so nothing it started keeps running in a game people play in.
/// </summary>
internal static class ProbeCalls
{
    /// <param name="owner">The probe or scenario assembly's name: what dvmp probes lists it under.</param>
    /// <param name="invoke">Calls the probe; its return value (an IEnumerator for a coroutine) goes to <paramref name="finish"/>.</param>
    /// <param name="finish">Main thread, once it and everything it started have ended: the result.</param>
    /// <param name="cancelled">Completes when the caller cancels (/v1/cancel); null for none.</param>
    /// <param name="output">What the probe logged so far, for a failure or a timeout.</param>
    /// <returns>The response: ok, the result or the error, and what was stopped with it.</returns>
    public static JObject Run(string owner, string name, Func<ProbeCoroutine, object> invoke, Func<object, JObject> finish,
        int timeoutMs, Task cancelled, Func<JToken> output)
    {
        ProbeCoroutine root = null;
        object returned = null;
        JArray stopped = new();
        Stopwatch clock = Stopwatch.StartNew();
        TaskCompletionSource<JObject> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Main thread, once the probe (and every coroutine it started) has ended.
        void Finish(ProbeCoroutine ended, Exception error)
        {
            stopped = StoppedList(ended);
            if (error != null) { tcs.TrySetException(error); return; }
            try
            {
                JObject o = finish(returned);
                AddStopped(o, stopped);
                tcs.TrySetResult(o);
            }
            catch (Exception e) { tcs.TrySetException(e); }
        }

        MainThread.Post(() =>
        {
            ProbeRegistry.InstallHook();
            root = ProbeRegistry.BeginProbe(owner, name);
            ProbeCoroutine previous = ProbeRegistry.Current;
            ProbeRegistry.Current = root;
            try { returned = invoke(root); }
            catch (Exception e)
            {
                ProbeRegistry.Current = previous;
                ProbeRegistry.EndProbe(root, "exception");
                Finish(root, e);
                return;
            }
            ProbeRegistry.Current = previous;

            if (returned is IEnumerator routine)
                ProbeRegistry.RunProbeRoutine(root, routine, r =>
                    Finish(r, r.Error ?? (r.EndReason == "completed" ? null : new ProbeStoppedException(r.EndReason))));
            else
            {
                ProbeRegistry.EndProbe(root, "completed");
                Finish(root, null);
            }
        });

        Task[] waits = cancelled != null ? new Task[] { tcs.Task, cancelled } : new Task[] { tcs.Task };
        int done = Task.WaitAny(waits, timeoutMs);
        if (done != 0)
            return Stop(() => root, done == 1 ? "cancelled" : $"its probe timed out after {timeoutMs} ms", done == 1, timeoutMs, output, clock);

        if (tcs.Task.IsFaulted)
        {
            Exception inner = tcs.Task.Exception?.InnerException;
            if (inner is TargetInvocationException tie) inner = tie.InnerException;
            JObject failed = new()
            {
                ["ok"] = false,
                ["error"] = inner?.GetType().Name + ": " + inner?.Message,
                ["stack"] = inner is ProbeStoppedException ? null : inner?.StackTrace,
                ["output"] = Safe(output),
                ["elapsedMs"] = clock.ElapsedMilliseconds
            };
            AddStopped(failed, stopped);
            return failed;
        }

        JObject response = tcs.Task.Result;
        response["elapsedMs"] = clock.ElapsedMilliseconds;
        response["ok"] = true;
        return response;
    }

    private sealed class ProbeStoppedException(string reason) : Exception("probe stopped: " + reason);

    // A probe that runs past its timeout, or is cancelled, is stopped with everything it started,
    // rather than left running in a game other people may be playing in.
    private static JObject Stop(Func<ProbeCoroutine> root, string reason, bool cancel, int timeoutMs, Func<JToken> output, Stopwatch clock)
    {
        JArray stopped = null;
        string note;
        try
        {
            Task<JArray> t = MainThread.Run(() =>
            {
                ProbeCoroutine r = root();
                if (r == null) return null;
                ProbeRegistry.EndProbe(r, reason);
                return StoppedList(r);
            });
            stopped = t.Wait(2000) ? t.Result : null;
            note = stopped != null
                ? "it was stopped, with every coroutine it started"
                : "the main thread is still busy (a probe stuck without yielding can't be stopped); it will be stopped when it next yields";
        }
        catch (Exception e) { note = "stopping it failed: " + e.Message; }
        if (stopped == null) MainThread.Post(() => { if (root() is ProbeCoroutine r) ProbeRegistry.EndProbe(r, reason); });

        JObject o = new()
        {
            ["ok"] = false,
            ["error"] = cancel ? $"cancelled; {note}" : $"probe did not finish within {timeoutMs} ms; {note}",
            ["output"] = Safe(output),
            ["elapsedMs"] = clock.ElapsedMilliseconds
        };
        o[cancel ? "cancelled" : "timedOut"] = true;
        AddStopped(o, stopped);
        return o;
    }

    private static JToken Safe(Func<JToken> output)
    {
        try { return output?.Invoke() ?? new JArray(); }
        catch { return new JArray(); }
    }

    private static JArray StoppedList(ProbeCoroutine root) =>
        new((root.StoppedWithIt ?? new List<ProbeCoroutine>()).Select(c => Json.ToToken(new { id = c.Id, name = c.Name, kind = c.Kind, host = c.HostPath })));

    private static void AddStopped(JObject o, JArray stopped)
    {
        if (stopped == null || stopped.Count == 0) return;
        o["stoppedCoroutines"] = stopped;
        o["stoppedHint"] = "coroutines a probe starts stop when it ends; for a watcher that should keep running use ctx.StartBackground(name, routine, maxSeconds), list it with dvmp_probes";
    }
}
