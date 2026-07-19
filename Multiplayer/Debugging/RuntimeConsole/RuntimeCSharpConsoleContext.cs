#if DEBUG
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Multiplayer.Debugging.RuntimeConsole;

/// <summary>
/// Stable, deliberately small ABI exposed to C# snippets compiled by the external
/// debug client.  Keeping this type independent from the scenario implementation
/// means console and scenario edits do not require rebuilding the game mod.
/// </summary>
public sealed class RuntimeCSharpConsoleContext
{
    private static readonly ConcurrentDictionary<string, object> state =
        new(StringComparer.Ordinal);
    private readonly List<object> output = new();

    internal RuntimeCSharpConsoleContext(string runId, string caseId,
        string sessionId, IReadOnlyDictionary<string, string> arguments)
    {
        RunId = runId ?? string.Empty;
        CaseId = caseId ?? string.Empty;
        SessionId = sessionId ?? string.Empty;
        Arguments = arguments ?? new Dictionary<string, string>();
    }

    public string RunId { get; }
    public string CaseId { get; }
    public string SessionId { get; }
    public IReadOnlyDictionary<string, string> Arguments { get; }
    public static ConcurrentDictionary<string, object> State => state;
    public IReadOnlyList<object> Output => output;
    public object Result { get; set; }

    public void Write(object value) => output.Add(value);
}
#endif
