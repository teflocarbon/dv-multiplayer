using System;
using System.Collections.Generic;
using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class TrackedStateComposerTests
{
    [Test]
    public void EmptyFullSyncIsValid()
    {
        Dictionary<string, object> result = TrackedStateComposer.Compose(
            Array.Empty<TrackedStateEntry>(), TrackedStateCompositionMode.FullSync, true);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void FullSyncIncludesCleanAndServerAuthoritativeValues()
    {
        Dictionary<string, object> result = TrackedStateComposer.Compose(Entries(), TrackedStateCompositionMode.FullSync, false);
        Assert.That(result.Keys, Is.EquivalentTo(new[] { "dirty", "clean", "authority" }));
    }

    [Test]
    public void ClientDeltaIncludesOnlyDirtyNonAuthoritativeValues()
    {
        Dictionary<string, object> result = TrackedStateComposer.Compose(Entries(), TrackedStateCompositionMode.Delta, false);
        Assert.That(result.Keys, Is.EquivalentTo(new[] { "dirty" }));
    }

    [Test]
    public void HostDeltaIncludesDirtyAuthoritativeValues()
    {
        Dictionary<string, object> result = TrackedStateComposer.Compose(Entries(), TrackedStateCompositionMode.Delta, true);
        Assert.That(result.Keys, Is.EquivalentTo(new[] { "dirty", "authority" }));
    }

    [Test]
    public void CompositionReturnsDetachedDictionary()
    {
        var entries = new List<TrackedStateEntry> { new("value", 1, true, false) };
        Dictionary<string, object> result = TrackedStateComposer.Compose(entries, TrackedStateCompositionMode.FullSync, true);
        entries[0] = new TrackedStateEntry("value", 2, true, false);
        Assert.That(result["value"], Is.EqualTo(1));
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase(null)]
    public void BlankKeysAreRejected(string key)
    {
        Assert.Throws<ArgumentException>(() => TrackedStateComposer.Compose(
            new[] { new TrackedStateEntry(key, 1, true, false) }, TrackedStateCompositionMode.FullSync, true));
    }

    [Test]
    public void DuplicateKeysAreRejectedInsteadOfSilentlyOverwritten()
    {
        var entries = new[] {
            new TrackedStateEntry("same", 1, true, false),
            new TrackedStateEntry("same", 2, true, false)
        };
        Assert.Throws<ArgumentException>(() => TrackedStateComposer.Compose(entries, TrackedStateCompositionMode.FullSync, true));
    }

    [Test]
    public void MergeSeparatesApplicableUnknownAndAuthorityRejectedValues()
    {
        var local = new[] {
            new TrackedStateEntry("normal", null, false, false),
            new TrackedStateEntry("authority", null, false, true)
        };
        var incoming = new Dictionary<string, object> {
            ["normal"] = 1, ["authority"] = 2, ["future"] = 3
        };
        TrackedStateMergePlan plan = TrackedStateComposer.PlanMerge(local, incoming, receiverIsHost: true);
        Assert.Multiple(() => {
            Assert.That(plan.ApplicableValues.Keys, Is.EquivalentTo(new[] { "normal" }));
            Assert.That(plan.AuthorityRejectedKeys, Is.EquivalentTo(new[] { "authority" }));
            Assert.That(plan.UnknownKeys, Is.EquivalentTo(new[] { "future" }));
        });
    }

    [Test]
    public void ClientAcceptsServerAuthoritativeMerge()
    {
        var local = new[] { new TrackedStateEntry("authority", null, false, true) };
        var incoming = new Dictionary<string, object> { ["authority"] = 5 };
        TrackedStateMergePlan plan = TrackedStateComposer.PlanMerge(local, incoming, receiverIsHost: false);
        Assert.That(plan.ApplicableValues["authority"], Is.EqualTo(5));
    }

    [Test]
    public void EveryDirtyAuthorityCombinationObeysSenderRules()
    {
        for (int mask = 0; mask < 256; mask++)
        {
            var entries = new List<TrackedStateEntry>();
            for (int index = 0; index < 4; index++)
            {
                bool dirty = (mask & (1 << (index * 2))) != 0;
                bool authority = (mask & (1 << (index * 2 + 1))) != 0;
                entries.Add(new TrackedStateEntry("v" + index, index, dirty, authority));
            }

            Dictionary<string, object> client = TrackedStateComposer.Compose(entries, TrackedStateCompositionMode.Delta, false);
            Dictionary<string, object> host = TrackedStateComposer.Compose(entries, TrackedStateCompositionMode.Delta, true);
            foreach (TrackedStateEntry entry in entries)
            {
                Assert.That(client.ContainsKey(entry.Key), Is.EqualTo(entry.IsDirty && !entry.ServerAuthoritative));
                Assert.That(host.ContainsKey(entry.Key), Is.EqualTo(entry.IsDirty));
            }
        }
    }

    private static TrackedStateEntry[] Entries() => new[] {
        new TrackedStateEntry("dirty", 1, true, false),
        new TrackedStateEntry("clean", 2, false, false),
        new TrackedStateEntry("authority", 3, true, true)
    };
}
