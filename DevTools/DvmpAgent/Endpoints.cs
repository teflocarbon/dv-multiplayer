using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Dvmp.Agent;

internal static class Endpoints
{
    private const int DefaultTimeoutMs = 30_000;
    private const int MaxTimeoutMs = 10 * 60_000;

    public static (int Status, string Body) Handle(string path, string body)
    {
        JObject request = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
        return path switch
        {
            "/v1/status" => Ok(Await(MainThread.Run(() => Json.ToToken(Status())), Timeout(request, 10_000), "status")),
            "/v1/eval" => Eval(request),
            "/v1/screenshot" => Ok(Await(MainThread.RunCoroutine<object>(tcs => Screenshot(request, tcs)), Timeout(request, 15_000), "screenshot")),
            "/v1/probes" => Ok(Await(MainThread.Run(() => Json.ToToken(Probes(request))), Timeout(request, 10_000), "probes")),
            "/v1/logs" => Ok(Logs(request)),
            "/v1/quit" => Ok(Await(MainThread.Run(Quit), 5_000, "quit")),
            _ => throw new HttpError(404, "unknown endpoint " + path)
        };
    }

    private static (int, string) Ok(object payload)
    {
        JObject o = payload as JObject ?? Json.ToToken(payload) as JObject ?? new JObject { ["value"] = Json.ToToken(payload) };
        o["ok"] = true;
        return (200, o.ToString(Newtonsoft.Json.Formatting.None));
    }

    private static int Timeout(JObject request, int fallback)
    {
        int ms = request.Value<int?>("timeoutMs") ?? fallback;
        return Math.Max(100, Math.Min(ms, MaxTimeoutMs));
    }

    private static T Await<T>(Task<T> task, int timeoutMs, string what)
    {
        if (!task.Wait(timeoutMs))
            throw new HttpError(504, $"{what}: main thread did not finish within {timeoutMs} ms (game frozen, loading, or probe too slow)");
        return task.Result;
    }

    // ---------------------------------------------------------------- status

    private static object Status() => new
    {
        pid = Process.GetCurrentProcess().Id,
        agentVersion = AgentMain.Version,
        role = Mp.Role(),
        scene = SceneManager.GetActiveScene().name,
        worldLoaded = SafeWorldLoaded(),
        loadingScreen = SafeLoadingScreen(),
        launchId = LaunchId(),
        logFile = Application.consoleLogPath,
        frame = Time.frameCount,
        fps = Math.Round(MainThread.Fps, 1),
        uptimeSeconds = Math.Round(Time.realtimeSinceStartup, 1),
        playerAbs = Dev.PlayerAbs,
        multiplayer = Mp.Snapshot(),
        focused = Application.isFocused,
        harness = HarnessMode.Snapshot(),
        probes = ProbeSummary(),
        screen = new { width = Screen.width, height = Screen.height }
    };

    private static bool? SafeWorldLoaded()
    {
        // Vanilla IsLoaded reports true when no world exists at all (bootstrap, main menu).
        try { return UnityEngine.Object.FindObjectOfType<WorldStreamingInit>() != null && WorldStreamingInit.IsLoaded; }
        catch { return null; }
    }

    // The ID dvmp launched this game with (-dvmp-launch), which names its game log and the mod's.
    private static string LaunchId()
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-dvmp-launch");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // In loading mode: from the loading screen starting until FinishLoading() (in VR, the hand
    // controllers and the teleport pointer are off until then).
    private static bool? SafeLoadingScreen()
    {
        try { return LoadingScreenManager.IsLoading; }
        catch { return null; }
    }

    // ---------------------------------------------------------------- eval

    private static (int, string) Eval(JObject request)
    {
        string assemblyBase64 = request.Value<string>("assembly") ?? throw new HttpError(400, "missing assembly");
        string typeName = request.Value<string>("type") ?? throw new HttpError(400, "missing type");
        Dictionary<string, string> args = request["args"] is JObject a
            ? a.Properties().ToDictionary(p => p.Name, p => p.Value.Type == JTokenType.String ? p.Value.Value<string>() : p.Value.ToString())
            : new Dictionary<string, string>();
        int timeoutMs = Timeout(request, DefaultTimeoutMs);

        // Every probe is a fresh, uniquely named assembly. Mono cannot unload it; that is fine
        // for a dev session but is why the agent never ships.
        Assembly assembly = Assembly.Load(Convert.FromBase64String(assemblyBase64));
        Type type = assembly.GetType(typeName, true);
        MethodInfo run = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(DevContext) }, null)
                         ?? throw new HttpError(400, $"{typeName} has no public static Run(DevContext)");

        DevContext context = new(args);
        string owner = assembly.GetName().Name;
        ProbeCoroutine root = null;
        object returned = null;
        int startFrame = 0;
        JArray stopped = new();
        Stopwatch clock = Stopwatch.StartNew();
        TaskCompletionSource<JObject> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Runs on the main thread once the probe (and every coroutine it started) has ended.
        void Finish(ProbeCoroutine ended, Exception error)
        {
            stopped = StoppedList(ended);
            if (error != null) { tcs.TrySetException(error); return; }
            try
            {
                object result = returned is IEnumerator ? context.Result : returned;
                JObject o = new()
                {
                    ["result"] = Json.ToBoundedToken(result, out bool truncated),
                    ["output"] = new JArray(context.Output.Select(Json.ToToken)),
                    ["frames"] = Time.frameCount - startFrame,
                    ["role"] = Mp.Role()
                };
                if (truncated) o["truncated"] = true;
                AddStopped(o, stopped);
                tcs.TrySetResult(o);
            }
            catch (Exception e) { tcs.TrySetException(e); }
        }

        MainThread.Post(() =>
        {
            ProbeRegistry.InstallHook();
            root = ProbeRegistry.BeginProbe(owner, ProbeName(typeName));
            context.Root = root;
            startFrame = Time.frameCount;
            ProbeCoroutine previous = ProbeRegistry.Current;
            ProbeRegistry.Current = root;
            try { returned = run.Invoke(null, new object[] { context }); }
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

        try
        {
            if (!tcs.Task.Wait(timeoutMs))
                return (200, TimedOut(() => root, context, timeoutMs, clock));
        }
        catch (AggregateException e)
        {
            Exception inner = e.InnerException is TargetInvocationException tie ? tie.InnerException : e.InnerException;
            JObject failed = new()
            {
                ["ok"] = false,
                ["error"] = inner?.GetType().Name + ": " + inner?.Message,
                ["stack"] = inner is ProbeStoppedException ? null : inner?.StackTrace,
                ["output"] = SafeOutput(context),
                ["elapsedMs"] = clock.ElapsedMilliseconds
            };
            AddStopped(failed, stopped);
            return (200, failed.ToString(Newtonsoft.Json.Formatting.None));
        }

        JObject response = tcs.Task.Result;
        response["elapsedMs"] = clock.ElapsedMilliseconds;
        response["ok"] = true;
        return (200, response.ToString(Newtonsoft.Json.Formatting.None));
    }

    private sealed class ProbeStoppedException(string reason) : Exception("probe stopped: " + reason);

    // "DvmpProbe_<guid>.Probe" says nothing; the guid is already the owner id.
    private static string ProbeName(string typeName) => typeName.Substring(typeName.LastIndexOf('.') + 1);

    // A probe that runs past its timeout is stopped, with everything it started, rather than
    // left running in a game other people may be playing in.
    private static string TimedOut(Func<ProbeCoroutine> root, DevContext context, int timeoutMs, Stopwatch clock)
    {
        JArray stopped = null;
        string note;
        try
        {
            Task<JArray> t = MainThread.Run(() =>
            {
                ProbeCoroutine r = root();
                if (r == null) return null;
                ProbeRegistry.EndProbe(r, $"its probe timed out after {timeoutMs} ms");
                return StoppedList(r);
            });
            stopped = t.Wait(2000) ? t.Result : null;
            note = stopped != null
                ? "it was stopped, with every coroutine it started"
                : "the main thread is still busy (a probe stuck without yielding can't be stopped); it will be stopped when it next yields";
        }
        catch (Exception e) { note = "stopping it failed: " + e.Message; }
        if (stopped == null) MainThread.Post(() => { if (root() is ProbeCoroutine r) ProbeRegistry.EndProbe(r, $"its probe timed out after {timeoutMs} ms"); });

        JObject o = new()
        {
            ["ok"] = false,
            ["timedOut"] = true,
            ["error"] = $"probe did not finish within {timeoutMs} ms; {note}",
            ["output"] = SafeOutput(context),
            ["elapsedMs"] = clock.ElapsedMilliseconds
        };
        AddStopped(o, stopped);
        return o.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static JArray StoppedList(ProbeCoroutine root) =>
        new((root.StoppedWithIt ?? new List<ProbeCoroutine>()).Select(c => Json.ToToken(new { id = c.Id, name = c.Name, kind = c.Kind, host = c.HostPath })));

    private static void AddStopped(JObject o, JArray stopped)
    {
        if (stopped == null || stopped.Count == 0) return;
        o["stoppedCoroutines"] = stopped;
        o["stoppedHint"] = "coroutines a probe starts stop when it ends; for a watcher that should keep running use ctx.StartBackground(name, routine, maxSeconds), list it with dvmp_probes";
    }

    private static JArray SafeOutput(DevContext context)
    {
        // Output may hold Unity objects, which can only be inspected on the main thread.
        try
        {
            Task<JArray> t = MainThread.Run(() => new JArray(context.Output.Select(Json.ToToken)));
            return t.Wait(2000) ? t.Result : new JArray();
        }
        catch { return new JArray(); }
    }

    // ---------------------------------------------------------------- probes

    // What probes left running in this process: coroutines (with their logs) and Harmony
    // patches, optionally stopping or removing some first.
    private static object Probes(JObject request)
    {
        string stop = request.Value<string>("stop");
        List<string> unpatch = request["unpatch"] is JArray u ? u.Select(t => t.Value<string>()).ToList() : new List<string>();

        List<ProbeCoroutine> stopped = ProbeRegistry.StopMatching(stop, "stopped by dvmp probes");
        if (stopped.Count > 0)
            Debug.Log($"[DvmpDevAgent] dvmp probes stopped {stopped.Count} probe coroutine(s): " + string.Join(", ", stopped.Select(c => $"#{c.Id} {c.Name}")));
        var (removed, refused) = unpatch.Count > 0 ? ProbePatches.Unpatch(unpatch) : (new List<string>(), new List<string>());
        if (removed.Count > 0)
            Debug.Log("[DvmpDevAgent] dvmp probes removed Harmony patches of " + string.Join(", ", removed));

        return new
        {
            coroutines = ProbeRegistry.Live.Select(c => c.Describe(withLog: true)).ToList(),
            recentlyEnded = ProbeRegistry.Recent.Reverse().Take(10).Select(c => c.Describe(withLog: false)).ToList(),
            patches = ProbePatches.Snapshot().Select(ProbePatches.Describe).ToList(),
            probeAssemblies = ProbePatches.ProbeAssemblyCount(),
            stopped = stop == null ? null : stopped.Select(c => new { id = c.Id, name = c.Name }).ToList(),
            unpatched = unpatch.Count == 0 ? null : removed,
            refused = refused.Count == 0 ? null : refused
        };
    }

    // The short form for status: enough for dvmp to warn about anything left running.
    private static object ProbeSummary()
    {
        try
        {
            List<ProbeCoroutine> live = ProbeRegistry.Live.ToList();
            return new
            {
                assemblies = ProbePatches.ProbeAssemblyCount(),
                inFlight = live.Count(c => c.Kind == "probe"),
                // Outlived their probe: background watchers, and hooked coroutines with no probe to stop with.
                lingering = live.Where(c => c.Kind == "background" || c.Kind == "hooked" && c.Parent == null)
                    .Select(c => new { id = c.Id, name = c.Name, kind = c.Kind, ageSeconds = Math.Round(Time.realtimeSinceStartup - c.StartedAt), maxSeconds = c.MaxSeconds > 0 ? c.MaxSeconds : (float?)null })
                    .ToList(),
                patches = ProbePatches.Snapshot().Where(o => o.Category is "probe" or "harness").Select(o => new { owner = o.Id, category = o.Category, methods = o.Methods }).ToList()
            };
        }
        catch (Exception e) { return new { error = e.Message }; }
    }

    // ---------------------------------------------------------------- screenshot

    private static IEnumerator Screenshot(JObject request, TaskCompletionSource<object> tcs)
    {
        int maxWidth = request.Value<int?>("maxWidth") ?? 1280;
        int quality = Mathf.Clamp(request.Value<int?>("quality") ?? 75, 10, 100);
        yield return new WaitForEndOfFrame();

        Texture2D captured = null, scaled = null;
        RenderTexture rt = null;
        try
        {
            captured = ScreenCapture.CaptureScreenshotAsTexture();
            Texture2D output = captured;
            if (maxWidth > 0 && captured.width > maxWidth)
            {
                int height = Mathf.Max(1, Mathf.RoundToInt(captured.height * (maxWidth / (float)captured.width)));
                rt = RenderTexture.GetTemporary(maxWidth, height, 0);
                Graphics.Blit(captured, rt);
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = rt;
                scaled = new Texture2D(maxWidth, height, TextureFormat.RGB24, false);
                scaled.ReadPixels(new Rect(0, 0, maxWidth, height), 0, 0);
                scaled.Apply();
                RenderTexture.active = previous;
                output = scaled;
            }
            byte[] jpeg = output.EncodeToJPG(quality);
            tcs.TrySetResult(new { width = output.width, height = output.height, mime = "image/jpeg", base64 = Convert.ToBase64String(jpeg) });
        }
        catch (Exception e)
        {
            tcs.TrySetException(e);
        }
        finally
        {
            if (captured != null) UnityEngine.Object.Destroy(captured);
            if (scaled != null) UnityEngine.Object.Destroy(scaled);
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
        }
    }

    // ---------------------------------------------------------------- logs

    private static object Logs(JObject request)
    {
        long since = request.Value<long?>("sinceSeq") ?? 0;
        string level = request.Value<string>("minLevel") ?? "log";
        string contains = request.Value<string>("contains");
        int max = Math.Max(1, Math.Min(request.Value<int?>("max") ?? 200, 2000));
        var (items, lastSeq, dropped) = LogBuffer.Query(since, level, contains, max);
        return new
        {
            lastSeq,
            dropped,
            entries = items.Select(e => new { seq = e.Seq, t = Math.Round(e.Time, 2), level = e.Level, message = e.Message, stack = e.Stack })
        };
    }

    // ---------------------------------------------------------------- quit

    private static object Quit()
    {
        Debug.Log("[DvmpDevAgent] quit requested");
        MainThreadQuit();
        return new { quitting = true };
    }

    private static void MainThreadQuit() => Application.Quit();
}
