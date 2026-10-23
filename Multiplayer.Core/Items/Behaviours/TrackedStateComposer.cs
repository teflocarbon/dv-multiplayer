using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Items.Behaviours;

public readonly struct TrackedStateEntry
{
    public TrackedStateEntry(string key, object value, bool isDirty, bool serverAuthoritative)
    {
        Key = key;
        Value = value;
        IsDirty = isDirty;
        ServerAuthoritative = serverAuthoritative;
    }

    public string Key { get; }
    public object Value { get; }
    public bool IsDirty { get; }
    public bool ServerAuthoritative { get; }
}

public enum TrackedStateCompositionMode
{
    Delta,
    FullSync
}

public sealed class TrackedStateMergePlan
{
    public TrackedStateMergePlan(
        IReadOnlyDictionary<string, object> applicableValues,
        IReadOnlyList<string> unknownKeys,
        IReadOnlyList<string> authorityRejectedKeys)
    {
        ApplicableValues = applicableValues;
        UnknownKeys = unknownKeys;
        AuthorityRejectedKeys = authorityRejectedKeys;
    }

    public IReadOnlyDictionary<string, object> ApplicableValues { get; }
    public IReadOnlyList<string> UnknownKeys { get; }
    public IReadOnlyList<string> AuthorityRejectedKeys { get; }
}

public static class TrackedStateComposer
{
    public static Dictionary<string, object> Compose(
        IEnumerable<TrackedStateEntry> entries,
        TrackedStateCompositionMode mode,
        bool senderIsHost)
    {
        if (entries == null) throw new ArgumentNullException(nameof(entries));
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (TrackedStateEntry entry in entries)
        {
            ValidateKey(entry.Key);
            if (mode == TrackedStateCompositionMode.Delta &&
                (!entry.IsDirty || (!senderIsHost && entry.ServerAuthoritative)))
                continue;
            if (result.ContainsKey(entry.Key))
                throw new ArgumentException($"Duplicate tracked-state key '{entry.Key}'.", nameof(entries));
            result.Add(entry.Key, entry.Value);
        }
        return result;
    }

    /// <summary>
    /// The values the host's copy of an item another player holds or carries may report: only the
    /// server's own. Its copy runs the item as well (a flashlight's battery drains in the player's
    /// hand, with no sun on its solar panel), but the holder's game is the one they see, so the
    /// rest are theirs to report; the host's drift overwrote them (playtest 3).
    /// </summary>
    public static Dictionary<string, object> HostReportsForRemoteHolder(
        IEnumerable<TrackedStateEntry> entries,
        IReadOnlyDictionary<string, object> states)
    {
        if (entries == null) throw new ArgumentNullException(nameof(entries));
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (states == null)
            return result;
        foreach (TrackedStateEntry entry in entries)
            if (entry.ServerAuthoritative && states.TryGetValue(entry.Key, out object value))
                result[entry.Key] = value;
        return result;
    }

    public static TrackedStateMergePlan PlanMerge(
        IEnumerable<TrackedStateEntry> localEntries,
        IReadOnlyDictionary<string, object> incoming,
        bool receiverIsHost)
    {
        if (localEntries == null) throw new ArgumentNullException(nameof(localEntries));
        if (incoming == null) throw new ArgumentNullException(nameof(incoming));

        var local = new Dictionary<string, TrackedStateEntry>(StringComparer.Ordinal);
        foreach (TrackedStateEntry entry in localEntries)
        {
            ValidateKey(entry.Key);
            if (local.ContainsKey(entry.Key))
                throw new ArgumentException($"Duplicate tracked-state key '{entry.Key}'.", nameof(localEntries));
            local.Add(entry.Key, entry);
        }

        var applicable = new Dictionary<string, object>(StringComparer.Ordinal);
        var unknown = new List<string>();
        var rejected = new List<string>();
        foreach (KeyValuePair<string, object> pair in incoming)
        {
            if (!local.TryGetValue(pair.Key, out TrackedStateEntry entry))
                unknown.Add(pair.Key);
            else if (receiverIsHost && entry.ServerAuthoritative)
                rejected.Add(pair.Key);
            else
                applicable.Add(pair.Key, pair.Value);
        }
        return new TrackedStateMergePlan(applicable, unknown, rejected);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Tracked-state keys cannot be blank.");
    }
}
