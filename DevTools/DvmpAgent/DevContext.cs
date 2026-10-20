using System;
using System.Collections;
using System.Collections.Generic;

namespace Dvmp.Agent;

/// <summary>
/// The object every probe receives. Keep this surface small and stable: probes are compiled
/// outside the game against whatever MultiplayerDevTools.dll is deployed.
/// </summary>
public sealed class DevContext
{
    private readonly List<object> output = new();

    internal DevContext(IReadOnlyDictionary<string, string> args) =>
        Args = args ?? new Dictionary<string, string>();

    /// <summary>String arguments passed alongside the probe.</summary>
    public IReadOnlyDictionary<string, string> Args { get; }

    /// <summary>
    /// Per-process scratch storage that survives between probes (main thread only).
    /// Useful for "remember this, then compare after the next action".
    /// </summary>
    public static Dictionary<string, object> State { get; } = new();

    /// <summary>Result reported when a coroutine probe finishes. A plain probe's return value wins.</summary>
    public object Result { get; set; }

    /// <summary>"host", "client", or "offline" for this process.</summary>
    public string Role => Mp.Role();

    /// <summary>Appends a value to the probe's output list.</summary>
    public void Log(object value) => output.Add(value);

    /// <summary>
    /// Runs <paramref name="routine"/> alongside the probe. It is stopped when the probe
    /// finishes, fails or times out, so it can never outlive it. Use this rather than
    /// MonoBehaviour.StartCoroutine (which the agent also catches and stops, with a warning).
    /// </summary>
    public ProbeCoroutine StartCoroutine(IEnumerator routine, string name = null) =>
        ProbeRegistry.StartChild(Root, routine, name);

    /// <summary>
    /// Runs <paramref name="routine"/> past the end of the probe, for watchers read by later
    /// probes. It stops after <paramref name="maxSeconds"/> (default 600, at most 3600), is
    /// listed by `dvmp probes` and flagged by `dvmp status` while it runs, and replaces any
    /// running background coroutine with the same name. Find it again with <see cref="Background"/>.
    /// </summary>
    public ProbeCoroutine StartBackground(string name, IEnumerator routine, float maxSeconds = ProbeRegistry.DefaultBackgroundSeconds) =>
        ProbeRegistry.StartBackground(Root?.Owner ?? "unknown", name, routine, maxSeconds);

    /// <summary>Like the other overload, but the routine gets its own handle, e.g. to call Log on it.</summary>
    public ProbeCoroutine StartBackground(string name, Func<ProbeCoroutine, IEnumerator> routine, float maxSeconds = ProbeRegistry.DefaultBackgroundSeconds)
    {
        Holder holder = new();
        ProbeCoroutine started = StartBackground(name, Deferred(holder, routine), maxSeconds);
        holder.Value = started;
        return started;
    }

    /// <summary>The running background coroutine with this name, if any.</summary>
    public static ProbeCoroutine Background(string name) => ProbeRegistry.FindBackground(name);

    internal ProbeCoroutine Root { get; set; }

    internal IReadOnlyList<object> Output => output;

    private sealed class Holder { public ProbeCoroutine Value; }

    // The handle only exists once StartBackground returns, after the routine's first step would
    // have run; wait a frame so the routine sees it.
    private static IEnumerator Deferred(Holder holder, Func<ProbeCoroutine, IEnumerator> routine)
    {
        yield return null;
        yield return routine(holder.Value);
    }
}
