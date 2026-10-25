using System;
using Multiplayer.Core.Items.LiveState;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.LiveState;

/// <summary>A snapped item's snap point by a key that survives a restart (OI-SYNC-03).</summary>
[TestFixture]
public sealed class SnapPointKeyTests
{
    [Test]
    public void AGadgetsKey_RoundTrips()
    {
        Guid hanger = new(7, 0, 0, new byte[8]);
        string key = SnapPointKey.OfGadget(hanger);

        Assert.That(SnapPointKey.TryGadget(key, out Guid back), Is.True);
        Assert.That(back, Is.EqualTo(hanger));
        Assert.That(key, Is.EqualTo(SnapPointKey.OfGadget(hanger)), "the same gadget, the same key");
    }

    [TestCase("")]
    [TestCase(null)]
    [TestCase("gadget:")]
    [TestCase("gadget:not-a-guid")]
    [TestCase("gadget:00000000-0000-0000-0000-000000000000")]
    [TestCase("shelf:7f2e")]
    public void AnythingElse_IsNoGadget(string key)
    {
        Assert.That(SnapPointKey.TryGadget(key, out Guid id), Is.False);
        Assert.That(id, Is.EqualTo(Guid.Empty));
    }

    [Test]
    public void NoGadget_HasNoKey() => Assert.That(SnapPointKey.OfGadget(Guid.Empty), Is.Empty);
}
