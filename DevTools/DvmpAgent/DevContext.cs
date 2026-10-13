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

    internal IReadOnlyList<object> Output => output;
}
