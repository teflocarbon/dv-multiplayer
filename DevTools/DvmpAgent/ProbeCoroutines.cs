using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Dvmp.Agent;

/// <summary>
/// A coroutine a probe started, through <see cref="DevContext.StartCoroutine"/>,
/// <see cref="DevContext.StartBackground(string, IEnumerator, float)"/>, or a plain
/// <c>MonoBehaviour.StartCoroutine</c> on a probe-made enumerator (caught by a hook).
/// <c>yield return</c> it to wait until it ends.
/// </summary>
public sealed class ProbeCoroutine : CustomYieldInstruction
{
    private const int MaxLog = 100;
    private readonly Stack<IEnumerator> stack = new();
    private readonly List<object> log = new();

    internal ProbeCoroutine(int id, string owner, string name, string kind, IEnumerator routine, ProbeCoroutine parent, float maxSeconds)
    {
        Id = id;
        Owner = owner;
        Name = name;
        Kind = kind;
        Parent = parent;
        MaxSeconds = maxSeconds;
        StartedAt = Time.realtimeSinceStartup;
        if (routine != null) stack.Push(routine);
    }

    public int Id { get; }
    /// <summary>The probe assembly that started it (dvmp_probe_...).</summary>
    public string Owner { get; }
    public string Name { get; }
    /// <summary>probe (a probe's own run), child (ctx.StartCoroutine), background, or hooked (MonoBehaviour.StartCoroutine).</summary>
    public string Kind { get; }
    public bool Running => EndReason == null;
    /// <summary>Why it ended: completed, an exception, or who stopped it. Null while running.</summary>
    public string EndReason { get; private set; }
    public Exception Error { get; private set; }

    /// <summary>Appends a value to this coroutine's log (newest 100 kept), shown by `dvmp probes`.</summary>
    public void Log(object value)
    {
        log.Add(value);
        if (log.Count > MaxLog) log.RemoveAt(0);
    }

    /// <summary>Stops it (and every coroutine it started) at once.</summary>
    public void Stop(string reason = "stopped by a probe") => ProbeRegistry.Stop(this, reason);

    public override bool keepWaiting => Running;

    internal ProbeCoroutine Parent { get; }
    internal float MaxSeconds { get; }
    internal float StartedAt { get; }
    internal float EndedAt { get; private set; }
    internal int Steps { get; private set; }
    internal float LastStepAt { get; private set; }
    internal MonoBehaviour Host { get; set; }
    internal Coroutine Handle { get; set; }
    internal string HostPath { get; set; }
    internal Action<ProbeCoroutine> OnEnd { get; set; }
    /// <summary>The coroutines it started that were still running when it ended (all stopped with it).</summary>
    internal List<ProbeCoroutine> StoppedWithIt { get; set; }
    internal IReadOnlyList<object> LogEntries => log;

    // The enumerator Unity drives. It steps the probe's own enumerator by hand so that a stop
    // takes effect between any two steps and exceptions are recorded rather than lost. Nested
    // enumerators (including CustomYieldInstruction such as WaitUntil) are flattened; other
    // yield values (WaitForSeconds, WaitForEndOfFrame, Coroutine, null) go to Unity unchanged.
    internal IEnumerator Driver()
    {
        while (Running)
        {
            if (MaxSeconds > 0 && Time.realtimeSinceStartup - StartedAt > MaxSeconds)
            {
                ProbeRegistry.Stop(this, $"ran past its {MaxSeconds:0} s limit");
                yield break;
            }
            if (!Step(out object current)) yield break;
            yield return current;
        }
    }

    private bool Step(out object current)
    {
        current = null;
        ProbeCoroutine previous = ProbeRegistry.Current;
        ProbeRegistry.Current = this;
        try
        {
            while (Running && stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                bool moved;
                try
                {
                    moved = top.MoveNext();
                    if (moved) current = top.Current;
                }
                catch (Exception e)
                {
                    ProbeRegistry.End(this, "exception: " + e.GetType().Name + ": " + e.Message, e);
                    return false;
                }
                if (!moved) { stack.Pop(); continue; }
                if (current is IEnumerator nested) { stack.Push(nested); continue; }
                Steps++;
                LastStepAt = Time.realtimeSinceStartup;
                return Running;
            }
            if (Running) ProbeRegistry.End(this, "completed", null);
            return false;
        }
        finally { ProbeRegistry.Current = previous; }
    }

    internal void Attach(IEnumerator routine) => stack.Push(routine);

    internal void MarkEnded(string reason, Exception error)
    {
        EndReason = reason;
        Error = error;
        EndedAt = Time.realtimeSinceStartup;
        // Runs the probe's finally blocks (e.g. Hands.ReleaseControl) for whatever was mid-flight.
        while (stack.Count > 0)
        {
            try { (stack.Pop() as IDisposable)?.Dispose(); }
            catch { }
        }
    }

    internal object Describe(bool withLog) => new
    {
        id = Id,
        name = Name,
        kind = Kind,
        probe = Owner,
        parent = Parent?.Id,
        running = Running,
        ended = EndReason,
        host = HostPath,
        ageSeconds = Math.Round((Running ? Time.realtimeSinceStartup : EndedAt) - StartedAt, 1),
        maxSeconds = MaxSeconds > 0 ? MaxSeconds : (float?)null,
        steps = Steps,
        lastStepSecondsAgo = Steps > 0 ? Math.Round(Time.realtimeSinceStartup - LastStepAt, 1) : (double?)null,
        log = withLog ? LogEntries.ToList() : null
    };
}

/// <summary>
/// Every coroutine a probe started, so none can keep running unnoticed after its probe ends.
/// A probe's own run is the root; coroutines it starts are its children and are stopped when it
/// finishes, fails or times out. Only background coroutines (with a time limit) outlive it.
/// Main thread only.
/// </summary>
internal static class ProbeRegistry
{
    public const string ProbeAssemblyPrefix = "dvmp_probe_";
    public const float DefaultBackgroundSeconds = 600;
    public const float MaxBackgroundSeconds = 3600;

    private const int MaxRecent = 30;
    private static readonly List<ProbeCoroutine> live = new();
    private static readonly List<ProbeCoroutine> recent = new();
    private static readonly Dictionary<Assembly, bool> probeAssemblies = new();
    private static int nextId = 1;
    private static float nextSweep;

    /// <summary>The coroutine (or probe run) whose code is executing right now.</summary>
    internal static ProbeCoroutine Current { get; set; }

    public static IReadOnlyList<ProbeCoroutine> Live => live;
    public static IReadOnlyList<ProbeCoroutine> Recent => recent;

    /// <summary>A C# scenario's assembly, loaded by /v1/load (ScenarioSteps): its steps are tracked as probes are.</summary>
    public const string ScenarioAssemblyPrefix = "DvmpScenarios_";

    public static bool IsProbeAssembly(Assembly assembly)
    {
        if (assembly == null) return false;
        if (!probeAssemblies.TryGetValue(assembly, out bool probe))
            probeAssemblies[assembly] = probe = IsProbeOwner(assembly.GetName().Name);
        return probe;
    }

    private static bool IsProbeOwner(string name) =>
        name.StartsWith(ProbeAssemblyPrefix, StringComparison.Ordinal) || name.StartsWith(ScenarioAssemblyPrefix, StringComparison.Ordinal);

    /// <summary>The root entry for one probe call; it is running until the probe's result is in.</summary>
    public static ProbeCoroutine BeginProbe(string owner, string name)
    {
        ProbeCoroutine root = new(nextId++, owner, name, "probe", null, null, 0);
        live.Add(root);
        return root;
    }

    /// <summary>Drives a coroutine probe's body as its root; <paramref name="onEnd"/> runs however it ends.</summary>
    public static void RunProbeRoutine(ProbeCoroutine root, IEnumerator routine, Action<ProbeCoroutine> onEnd)
    {
        root.Attach(routine);
        root.OnEnd = onEnd;
        Launch(root, MainThread.Instance);
    }

    public static ProbeCoroutine StartChild(ProbeCoroutine parent, IEnumerator routine, string name)
    {
        if (routine == null) throw new ArgumentNullException(nameof(routine));
        if (parent == null || !parent.Running) throw new InvalidOperationException("the probe that would own this coroutine has already ended; use ctx.StartBackground");
        ProbeCoroutine child = new(nextId++, parent.Owner, name ?? NameOf(routine), "child", routine, parent, 0);
        live.Add(child);
        Launch(child, MainThread.Instance);
        return child;
    }

    public static ProbeCoroutine StartBackground(string owner, string name, IEnumerator routine, float maxSeconds)
    {
        if (routine == null) throw new ArgumentNullException(nameof(routine));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("a background coroutine needs a name", nameof(name));
        float limit = maxSeconds <= 0 ? DefaultBackgroundSeconds : Math.Min(maxSeconds, MaxBackgroundSeconds);
        // Re-running a probe replaces its watcher instead of stacking a second one.
        foreach (ProbeCoroutine old in live.Where(c => c.Kind == "background" && c.Name == name).ToList())
            Stop(old, "replaced by a new background coroutine of the same name");
        ProbeCoroutine bg = new(nextId++, owner, name, "background", routine, null, limit);
        live.Add(bg);
        Launch(bg, MainThread.Instance);
        Debug.Log($"[DvmpDevAgent] background probe coroutine '{name}' started (#{bg.Id}, stops after {limit:0} s)");
        return bg;
    }

    public static ProbeCoroutine FindBackground(string name) =>
        live.LastOrDefault(c => c.Kind == "background" && c.Name == name);

    private static void Launch(ProbeCoroutine c, MonoBehaviour host)
    {
        c.Host = host;
        c.HostPath = Dev.Path(host);
        Coroutine handle = host.StartCoroutine(c.Driver());
        if (c.Running) c.Handle = handle;
    }

    public static void Stop(ProbeCoroutine c, string reason)
    {
        if (c == null || !c.Running) return;
        if (c.Host != null && c.Handle != null && c != Current)
        {
            try { c.Host.StopCoroutine(c.Handle); }
            catch { }
        }
        End(c, reason, null);
    }

    internal static void End(ProbeCoroutine c, string reason, Exception error)
    {
        if (!c.Running) return;
        List<ProbeCoroutine> descendants = Descendants(c).ToList();
        c.MarkEnded(reason, error);
        live.Remove(c);
        if (c.Kind != "probe" || reason != "completed")
        {
            recent.Add(c);
            if (recent.Count > MaxRecent) recent.RemoveAt(0);
        }
        if (error != null && c.Kind != "probe")
            Debug.LogWarning($"[DvmpDevAgent] probe coroutine #{c.Id} '{c.Name}' ({c.Owner}) failed: {error}");

        string why = reason == "completed" ? $"its parent #{c.Id} finished" : $"its parent #{c.Id} ended ({reason})";
        foreach (ProbeCoroutine child in live.Where(x => x.Parent == c).ToList())
            Stop(child, why);
        c.StoppedWithIt = descendants;

        try { c.OnEnd?.Invoke(c); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>Ends a probe call and every coroutine it still has running; returns those.</summary>
    public static List<ProbeCoroutine> EndProbe(ProbeCoroutine root, string reason)
    {
        Stop(root, reason);
        return root.StoppedWithIt ?? new List<ProbeCoroutine>();
    }

    private static IEnumerable<ProbeCoroutine> Descendants(ProbeCoroutine root)
    {
        foreach (ProbeCoroutine c in live.ToList())
            for (ProbeCoroutine p = c.Parent; p != null; p = p.Parent)
                if (p == root) { yield return c; break; }
    }

    /// <summary>Stops coroutines matching "all", an id, a name, or a probe id prefix.</summary>
    public static List<ProbeCoroutine> StopMatching(string selector, string reason)
    {
        if (string.IsNullOrWhiteSpace(selector)) return new List<ProbeCoroutine>();
        selector = selector.Trim();
        List<ProbeCoroutine> matched = live.Where(c =>
            selector == "all" ||
            (int.TryParse(selector, out int id) && c.Id == id) ||
            c.Name == selector ||
            (IsProbeOwner(selector) && c.Owner.StartsWith(selector, StringComparison.Ordinal))).ToList();
        foreach (ProbeCoroutine c in matched) Stop(c, reason);
        return matched;
    }

    // Called every frame; cheap unless something needs stopping. Catches background coroutines
    // waiting out a long yield past their limit, and hooked ones whose host object went away
    // (Unity stops those silently).
    public static void Sweep()
    {
        if (live.Count == 0 || Time.realtimeSinceStartup < nextSweep) return;
        nextSweep = Time.realtimeSinceStartup + 1f;
        foreach (ProbeCoroutine c in live.ToList())
        {
            if (c.MaxSeconds > 0 && Time.realtimeSinceStartup - c.StartedAt > c.MaxSeconds)
                Stop(c, $"ran past its {c.MaxSeconds:0} s limit");
            else if (c.Kind == "hooked" && c.Handle == null)
                End(c, "StartCoroutine failed", null); // e.g. its object was inactive; the postfix never ran
            else if (c.Handle != null && (c.Host == null || !c.Host.gameObject.activeInHierarchy))
                End(c, "its host object was destroyed or deactivated", null);
        }
    }

    private static string NameOf(IEnumerator routine)
    {
        // Compiler-generated iterators are named like "<Watch>d__3".
        string n = routine.GetType().Name;
        int open = n.IndexOf('<'), close = n.IndexOf('>');
        return open >= 0 && close > open + 1 ? n.Substring(open + 1, close - open - 1) : n;
    }

    // ---------------------------------------------------------------- MonoBehaviour.StartCoroutine hook

    private static bool hookInstalled;

    /// <summary>
    /// Catches probes that call MonoBehaviour.StartCoroutine directly with their own enumerator,
    /// so those coroutines are tracked and stopped with their probe too. Installed on the first
    /// probe call, so a game nobody probes never pays for it.
    /// </summary>
    public static void InstallHook()
    {
        if (hookInstalled) return;
        hookInstalled = true;
        try
        {
            Harmony harmony = new("dvmp.agent.coroutines");
            harmony.Patch(AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.StartCoroutine), new[] { typeof(IEnumerator) }),
                prefix: new HarmonyMethod(typeof(ProbeRegistry), nameof(BeforeStartCoroutine)),
                postfix: new HarmonyMethod(typeof(ProbeRegistry), nameof(AfterStartCoroutine)));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DvmpDevAgent] could not hook MonoBehaviour.StartCoroutine; probe coroutines started that way are not tracked: " + e.Message);
        }
    }

    private static void BeforeStartCoroutine(MonoBehaviour __instance, ref IEnumerator __0, out ProbeCoroutine __state)
    {
        __state = null;
        IEnumerator routine = __0;
        if (routine == null || !IsProbeAssembly(routine.GetType().Assembly)) return;

        string owner = routine.GetType().Assembly.GetName().Name;
        // Owned by whatever probe code is running now; failing that (e.g. a plain probe's Run),
        // by that probe's call while it is in flight. Anything else (say, a probe's Harmony
        // patch starting coroutines from game callbacks) has no owner and is listed as such.
        ProbeCoroutine parent = Current ?? live.LastOrDefault(c => c.Kind == "probe" && c.Owner == owner);
        ProbeCoroutine hooked = new(nextId++, owner, NameOf(routine), "hooked", routine, parent, 0)
        {
            Host = __instance,
            HostPath = Dev.Path(__instance)
        };
        live.Add(hooked);
        Debug.LogWarning($"[DvmpDevAgent] probe {owner} started coroutine '{hooked.Name}' with StartCoroutine on {hooked.HostPath}; " +
                         (parent != null ? $"it stops with #{parent.Id}" : "it has no probe to stop with") +
                         ". Use ctx.StartCoroutine or ctx.StartBackground instead.");
        __0 = hooked.Driver();
        __state = hooked;
    }

    private static void AfterStartCoroutine(Coroutine __result, ProbeCoroutine __state)
    {
        if (__state != null && __state.Running) __state.Handle = __result;
    }
}
