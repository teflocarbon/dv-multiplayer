using Multiplayer.Core.Trains;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Trains;

[TestFixture]
public sealed class TrackSpansTests
{
    [Test]
    public void PastTheEnd_OntoATrackJoinedAtItsStart()
    {
        Assert.That(TrackSpans.TryCross(105, 100, first: true, nextLength: 50, out double span), Is.True);
        Assert.That(span, Is.EqualTo(5).Within(1e-9));
    }

    [Test]
    public void PastTheEnd_OntoATrackJoinedAtItsEnd_RunsBackwards()
    {
        Assert.That(TrackSpans.TryCross(105, 100, first: false, nextLength: 50, out double span), Is.True);
        Assert.That(span, Is.EqualTo(45).Within(1e-9));
    }

    [Test]
    public void PastTheStart()
    {
        Assert.That(TrackSpans.TryCross(-5, 100, first: true, nextLength: 50, out double span), Is.True);
        Assert.That(span, Is.EqualTo(5).Within(1e-9));
        Assert.That(TrackSpans.TryCross(-5, 100, first: false, nextLength: 50, out span), Is.True);
        Assert.That(span, Is.EqualTo(45).Within(1e-9));
    }

    [Test]
    public void PastTheNextTrackToo_IsNotCrossed()
    {
        Assert.That(TrackSpans.TryCross(160, 100, first: true, nextLength: 50, out _), Is.False);
        Assert.That(TrackSpans.TryCross(-60, 100, first: false, nextLength: 50, out _), Is.False);
    }

    [TestCase(105, true)]
    [TestCase(105, false)]
    [TestCase(-5, true)]
    [TestCase(-5, false)]
    public void FromNext_UndoesTryCross(double span, bool first)
    {
        Assert.That(TrackSpans.TryCross(span, 100, first, 50, out double next), Is.True);
        Assert.That(TrackSpans.FromNext(next, 100, atEnd: span > 0, first, 50), Is.EqualTo(span).Within(1e-9));
    }
}
