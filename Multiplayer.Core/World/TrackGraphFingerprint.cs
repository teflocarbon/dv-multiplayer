using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.World;

/// <summary>
/// A hash of a game's NetId → GameObject-name tables for rail tracks, junctions and turntables.
/// Every game numbers these as they wake, so two games agree on them only if they built the same
/// track graph in the same order. The host compares a joining client's fingerprint with its own
/// and refuses the join on a difference, rather than letting cars land on the wrong track.
/// </summary>
public readonly struct TrackGraphFingerprint : IEquatable<TrackGraphFingerprint>
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public TrackGraphFingerprint(ulong hash, int tracks, int junctions, int turntables)
    {
        Hash = hash;
        Tracks = tracks;
        Junctions = junctions;
        Turntables = turntables;
    }

    public ulong Hash { get; }
    public int Tracks { get; }
    public int Junctions { get; }
    public int Turntables { get; }

    /// <summary>
    /// Hashes the three tables. Entries may come in any order: each table is sorted by NetId (then
    /// name) first, so only which name has which NetId counts.
    /// </summary>
    public static TrackGraphFingerprint Compute(
        IEnumerable<KeyValuePair<ushort, string>> tracks,
        IEnumerable<KeyValuePair<ushort, string>> junctions,
        IEnumerable<KeyValuePair<ushort, string>> turntables)
    {
        ulong hash = FnvOffset;
        int trackCount = AddTable(ref hash, 1, tracks);
        int junctionCount = AddTable(ref hash, 2, junctions);
        int turntableCount = AddTable(ref hash, 3, turntables);
        return new TrackGraphFingerprint(hash, trackCount, junctionCount, turntableCount);
    }

    /// <summary>
    /// Why a client's graph (<paramref name="client"/>) can't join this host's, for the player and
    /// the logs; null when they match.
    /// </summary>
    public string DescribeMismatch(TrackGraphFingerprint client)
    {
        if (Equals(client))
            return null;

        string counts = Tracks == client.Tracks && Junctions == client.Junctions && Turntables == client.Turntables
            ? $"Both have {Describe(this)}, but they are numbered differently."
            : $"The host has {Describe(this)}; you have {Describe(client)}.";
        return "Your railway doesn't match the host's, so trains would be on the wrong track. " + counts +
               " Check that you both run the same mods that add or change track.";
    }

    public bool Equals(TrackGraphFingerprint other) =>
        Hash == other.Hash && Tracks == other.Tracks && Junctions == other.Junctions && Turntables == other.Turntables;

    public override bool Equals(object obj) => obj is TrackGraphFingerprint other && Equals(other);

    public override int GetHashCode() => Hash.GetHashCode();

    public override string ToString() =>
        $"{Hash:X16} ({Tracks} tracks, {Junctions} junctions, {Turntables} turntables)";

    private static string Describe(TrackGraphFingerprint graph) =>
        $"{graph.Tracks} tracks, {graph.Junctions} junctions and {graph.Turntables} turntables";

    private static int AddTable(ref ulong hash, byte kind, IEnumerable<KeyValuePair<ushort, string>> table)
    {
        KeyValuePair<ushort, string>[] sorted = (table ?? Enumerable.Empty<KeyValuePair<ushort, string>>())
            .OrderBy(entry => entry.Key)
            .ThenBy(entry => entry.Value, StringComparer.Ordinal)
            .ToArray();

        AddByte(ref hash, kind);
        AddInt(ref hash, sorted.Length);
        foreach (KeyValuePair<ushort, string> entry in sorted)
        {
            AddByte(ref hash, (byte)entry.Key);
            AddByte(ref hash, (byte)(entry.Key >> 8));
            // Length first, so ("ab", "c") and ("a", "bc") hash apart.
            string name = entry.Value ?? string.Empty;
            AddInt(ref hash, name.Length);
            foreach (char c in name)
            {
                AddByte(ref hash, (byte)c);
                AddByte(ref hash, (byte)(c >> 8));
            }
        }
        return sorted.Length;
    }

    private static void AddInt(ref ulong hash, int value)
    {
        for (int shift = 0; shift < 32; shift += 8)
            AddByte(ref hash, (byte)(value >> shift));
    }

    private static void AddByte(ref ulong hash, byte value)
    {
        hash ^= value;
        hash *= FnvPrime;
    }
}
