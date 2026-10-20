using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.World;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.World;

[TestFixture]
public sealed class TrackGraphFingerprintTests
{
    private static readonly KeyValuePair<ushort, string>[] Tracks =
    {
        Entry(1, "[Y]_[SM]_[A-01-L]"), Entry(2, "[Y]_[SM]_[A-02-L]"), Entry(3, "[#] SM-FF"),
    };
    private static readonly KeyValuePair<ushort, string>[] Junctions = { Entry(1, "junc-1"), Entry(2, "junc-2") };
    private static readonly KeyValuePair<ushort, string>[] Turntables = { Entry(1, "TurntableSM") };

    [Test]
    public void SameTablesInAnyOrderMatch()
    {
        TrackGraphFingerprint host = TrackGraphFingerprint.Compute(Tracks, Junctions, Turntables);
        TrackGraphFingerprint client = TrackGraphFingerprint.Compute(Tracks.Reverse(), Junctions.Reverse(), Turntables);

        Assert.That(client, Is.EqualTo(host));
        Assert.That(host.DescribeMismatch(client), Is.Null);
        Assert.That((host.Tracks, host.Junctions, host.Turntables), Is.EqualTo((3, 2, 1)));
    }

    [Test]
    public void SwappedNetIdsDontMatchAndSayTheCountsAgree()
    {
        TrackGraphFingerprint host = TrackGraphFingerprint.Compute(Tracks, Junctions, Turntables);
        TrackGraphFingerprint client = TrackGraphFingerprint.Compute(
            new[] { Entry(1, Tracks[1].Value), Entry(2, Tracks[0].Value), Tracks[2] }, Junctions, Turntables);

        Assert.That(client.Hash, Is.Not.EqualTo(host.Hash));
        Assert.That(host.DescribeMismatch(client), Does.Contain("numbered differently"));
    }

    [Test]
    public void AnExtraTrackDoesntMatchAndGivesBothCounts()
    {
        TrackGraphFingerprint host = TrackGraphFingerprint.Compute(Tracks, Junctions, Turntables);
        TrackGraphFingerprint client = TrackGraphFingerprint.Compute(Tracks.Append(Entry(4, "modded")), Junctions, Turntables);

        string reason = host.DescribeMismatch(client);
        Assert.That(reason, Does.Contain("The host has 3 tracks"));
        Assert.That(reason, Does.Contain("you have 4 tracks"));
    }

    [Test]
    public void RenamedTrackDoesntMatch()
    {
        TrackGraphFingerprint host = TrackGraphFingerprint.Compute(Tracks, Junctions, Turntables);
        TrackGraphFingerprint client = TrackGraphFingerprint.Compute(
            new[] { Tracks[0], Tracks[1], Entry(3, "[#] SM-FG") }, Junctions, Turntables);

        Assert.That(host.DescribeMismatch(client), Is.Not.Null);
    }

    [Test]
    public void TablesAreHashedApart()
    {
        // The same entry as a junction rather than a turntable is a different graph.
        TrackGraphFingerprint asJunction = TrackGraphFingerprint.Compute(Tracks, Junctions.Append(Entry(3, "x")), Turntables);
        TrackGraphFingerprint asTurntable = TrackGraphFingerprint.Compute(Tracks, Junctions, Turntables.Append(Entry(3, "x")));

        Assert.That(asJunction.Hash, Is.Not.EqualTo(asTurntable.Hash));
    }

    [Test]
    public void NameBoundariesCount()
    {
        TrackGraphFingerprint a = TrackGraphFingerprint.Compute(new[] { Entry(1, "ab"), Entry(2, "c") }, null, null);
        TrackGraphFingerprint b = TrackGraphFingerprint.Compute(new[] { Entry(1, "a"), Entry(2, "bc") }, null, null);

        Assert.That(a.Hash, Is.Not.EqualTo(b.Hash));
    }

    [Test]
    public void AClientThatSentNoGraphDoesntMatch()
    {
        TrackGraphFingerprint host = TrackGraphFingerprint.Compute(Tracks, Junctions, Turntables);

        Assert.That(host.DescribeMismatch(default), Does.Contain("you have 0 tracks"));
    }

    [Test]
    public void HashIsStableAcrossRuns()
    {
        // Host and client are separate processes: the hash must not depend on string.GetHashCode
        // or anything else that can vary per run.
        TrackGraphFingerprint graph = TrackGraphFingerprint.Compute(new[] { Entry(1, "a") }, null, null);

        Assert.That(graph.Hash, Is.EqualTo(TrackGraphFingerprint.Compute(new[] { Entry(1, "a") }, null, null).Hash));
        Assert.That(graph.Hash.ToString("X16"), Is.EqualTo(ExpectedHashOfOneTrackNamedA()));
    }

    private static string ExpectedHashOfOneTrackNamedA()
    {
        // FNV-1a 64 over: kind 1, count 1 (int32 LE), NetId 1 (uint16 LE), length 1 (int32 LE),
        // 'a' (UTF-16 LE), then the empty junction (kind 2) and turntable (kind 3) tables.
        byte[] bytes = { 1, 1, 0, 0, 0, 1, 0, 1, 0, 0, 0, (byte)'a', 0, 2, 0, 0, 0, 0, 3, 0, 0, 0, 0 };
        ulong hash = 14695981039346656037UL;
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash.ToString("X16");
    }

    private static KeyValuePair<ushort, string> Entry(ushort netId, string name) => new(netId, name);
}
