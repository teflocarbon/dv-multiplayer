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
        frame = Time.frameCount,
        fps = Math.Round(MainThread.Fps, 1),
        uptimeSeconds = Math.Round(Time.realtimeSinceStartup, 1),
        playerAbs = Dev.PlayerAbs,
        multiplayer = Mp.Snapshot(),
        focused = Application.isFocused,
        harness = HarnessMode.Snapshot(),
        screen = new { width = Screen.width, height = Screen.height }
    };

    private static bool? SafeWorldLoaded()
    {
        // Vanilla IsLoaded reports true when no world exists at all (bootstrap, main menu).
        try { return UnityEngine.Object.FindObjectOfType<WorldStreamingInit>() != null && WorldStreamingInit.IsLoaded; }
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
        bool cancelled = false;
        object returned = null;
        int startFrame = 0;
        Stopwatch clock = Stopwatch.StartNew();

        Task<JObject> task = MainThread.RunMaybeCoroutine(
            start: () =>
            {
                startFrame = Time.frameCount;
                returned = run.Invoke(null, new object[] { context });
                return returned;
            },
            finish: () =>
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
                return o;
            },
            cancelled: () => cancelled);

        try
        {
            if (!task.Wait(timeoutMs))
            {
                cancelled = true;
                throw new HttpError(504, $"probe did not finish within {timeoutMs} ms");
            }
        }
        catch (AggregateException e)
        {
            Exception inner = e.InnerException is TargetInvocationException tie ? tie.InnerException : e.InnerException;
            return (200, new JObject
            {
                ["ok"] = false,
                ["error"] = inner?.GetType().Name + ": " + inner?.Message,
                ["stack"] = inner?.StackTrace,
                ["output"] = SafeOutput(context),
                ["elapsedMs"] = clock.ElapsedMilliseconds
            }.ToString(Newtonsoft.Json.Formatting.None));
        }

        JObject response = task.Result;
        response["elapsedMs"] = clock.ElapsedMilliseconds;
        response["ok"] = true;
        return (200, response.ToString(Newtonsoft.Json.Formatting.None));
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
