using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Dvmp.Agent;

/// <summary>
/// Runs the steps of compiled C# scenarios (DevTools/DvmpScenarios, docs/design/scenarios.md).
/// Unlike a probe, the scenario assembly is loaded once per game process (keyed by its SHA-256)
/// and its [Step] methods are called by name with JSON arguments, so a run compiles nothing.
/// The agent knows no scenario types: a coroutine step's result object is whatever its first
/// parameter is, when that parameter's type is called Step`1 (it has Result and Notes).
/// </summary>
internal static class ScenarioSteps
{
    private const int DefaultTimeoutMs = 60_000;
    private const int MaxTimeoutMs = 10 * 60_000;

    private static readonly ConcurrentDictionary<string, Assembly> loaded = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> running = new();
    private static readonly JsonSerializer json = JsonSerializer.Create(new JsonSerializerSettings
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        MaxDepth = 32
    });

    /// <summary>
    /// {hash, assembly?, expect?: {name: mvid}}. Without the assembly, 404 unless that hash is
    /// already loaded. expect names assemblies the scenarios were built against: one this game
    /// loaded with another MVID (a stale install) is refused with 409.
    /// </summary>
    public static (int, string) Load(JObject request)
    {
        string hash = request.Value<string>("hash") ?? throw new HttpError(400, "missing hash");
        if (request["expect"] is JObject expect)
            foreach (JProperty p in expect.Properties())
            {
                Assembly game = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == p.Name);
                string mvid = game?.ManifestModule.ModuleVersionId.ToString();
                if (game != null && !string.Equals(mvid, p.Value.Value<string>(), StringComparison.OrdinalIgnoreCase))
                    throw new HttpError(409, $"this game runs a different {p.Name}.dll ({mvid}) than the scenarios were built against " +
                                             $"({p.Value}): rebuild, or `dvmp up --fresh` to deploy the build");
            }
        if (loaded.ContainsKey(hash)) return Ok(new JObject { ["loaded"] = true });

        string base64 = request.Value<string>("assembly");
        if (base64 == null) throw new HttpError(404, "scenario assembly " + hash + " is not loaded");
        byte[] bytes = Convert.FromBase64String(base64);
        using (SHA256 sha = SHA256.Create())
        {
            string actual = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
            if (actual != hash) throw new HttpError(400, $"assembly hash is {actual}, not {hash}");
        }
        // Mono can't unload it; each build has its own name (the runner renames it), so a game
        // that stays up across rebuilds holds one copy of each.
        loaded[hash] = Assembly.Load(bytes);
        UnityEngine.Debug.Log($"[DvmpDevAgent] loaded scenario assembly {loaded[hash].GetName().Name}");
        return Ok(new JObject { ["loaded"] = true, ["uploaded"] = true });
    }

    /// <summary>{hash, call, type, method, parameters: [signature], args: [json], timeoutMs}</summary>
    public static (int, string) Invoke(JObject request)
    {
        string hash = request.Value<string>("hash") ?? throw new HttpError(400, "missing hash");
        if (!loaded.TryGetValue(hash, out Assembly assembly))
            throw new HttpError(404, "scenario assembly " + hash + " is not loaded");
        string call = request.Value<string>("call") ?? Guid.NewGuid().ToString("N");
        string typeName = request.Value<string>("type") ?? throw new HttpError(400, "missing type");
        string methodName = request.Value<string>("method") ?? throw new HttpError(400, "missing method");
        string[] signature = request["parameters"]?.Values<string>().ToArray() ?? Array.Empty<string>();
        JArray args = request["args"] as JArray ?? new JArray();
        int timeoutMs = Math.Max(100, Math.Min(request.Value<int?>("timeoutMs") ?? DefaultTimeoutMs, MaxTimeoutMs));

        Type type = assembly.GetType(typeName, true);
        MethodInfo method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Select(p => Signature(p.ParameterType)).SequenceEqual(signature))
            ?? throw new HttpError(400, $"{typeName} has no static {methodName}({string.Join(", ", signature)})");
        ParameterInfo[] parameters = method.GetParameters();
        bool hasStep = parameters.Length > 0 && IsStep(parameters[0].ParameterType);
        if (args.Count != parameters.Length - (hasStep ? 1 : 0))
            throw new HttpError(400, $"{methodName} takes {parameters.Length - (hasStep ? 1 : 0)} argument(s), got {args.Count}");

        object step = hasStep ? Activator.CreateInstance(parameters[0].ParameterType) : null;
        object[] values = parameters.Select((p, i) => i == 0 && hasStep ? step : args[i - (hasStep ? 1 : 0)].ToObject(p.ParameterType, json)).ToArray();

        // A tracked root, as a probe is: every coroutine the step starts (through a hooked
        // MonoBehaviour.StartCoroutine) stops with it, and a cancel stops it and them at once.
        TaskCompletionSource<bool> cancel = new();
        running[call] = cancel;
        try
        {
            JObject response = ProbeCalls.Run(assembly.GetName().Name, $"{type.Name}.{methodName}",
                invoke: _ => method.Invoke(null, values),
                finish: returned =>
                {
                    object result = returned is IEnumerator ? step?.GetType().GetProperty("Result")?.GetValue(step) : returned;
                    return new JObject { ["result"] = result == null ? JValue.CreateNull() : JToken.FromObject(result, json) };
                },
                timeoutMs, cancel.Task, output: () => new JArray());
            response.Remove("output");
            response["notes"] = Notes(step);
            if (response.Value<bool?>("ok") != true && response["error"] is JValue error)
                response["error"] = $"{methodName}: {error}";
            return (200, response.ToString(Formatting.None));
        }
        finally
        {
            running.TryRemove(call, out _);
        }
    }

    /// <summary>{call}: stops a running step and every coroutine it started; its invoke returns cancelled.</summary>
    public static (int, string) Cancel(JObject request)
    {
        string call = request.Value<string>("call") ?? throw new HttpError(400, "missing call");
        bool found = running.TryGetValue(call, out TaskCompletionSource<bool> cancel);
        cancel?.TrySetResult(true);
        return Ok(new JObject { ["cancelled"] = found });
    }

    /// <summary>A parameter type as the runner names it: no assembly names, so a renamed
    /// scenario assembly still matches. Keep in step with DvmpScenarios/Runtime/StepCall.cs.</summary>
    internal static string Signature(Type t) =>
        t.IsArray ? Signature(t.GetElementType()) + "[]"
        : t.IsGenericType ? t.GetGenericTypeDefinition().FullName + "<" + string.Join(",", t.GetGenericArguments().Select(Signature)) + ">"
        : t.FullName;

    private static bool IsStep(Type t) => t.IsGenericType && t.GetGenericTypeDefinition().Name == "Step`1";

    private static JArray Notes(object step)
    {
        // Main thread only in principle; notes are plain strings appended by the step itself.
        try { return step?.GetType().GetProperty("Notes")?.GetValue(step) is IEnumerable<string> notes ? new JArray(notes.ToArray()) : new JArray(); }
        catch { return new JArray(); }
    }

    private static (int, string) Ok(JObject o)
    {
        o["ok"] = true;
        return (200, o.ToString(Formatting.None));
    }
}
