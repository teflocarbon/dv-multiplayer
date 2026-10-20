using Multiplayer.Core.Trains;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Trains;

[TestFixture]
public sealed class FreezeCatchUpTests
{
    [Test]
    public void AFrameThatLostSecondsOfGameTime_IsAFreeze()
    {
        // The train-rate run: a 3.25 s frame, game time capped.
        Assert.That(FreezeCatchUp.IsFreeze(3.25f, 0.1f), Is.True);
    }

    [Test]
    public void LowFrameRates_AreNotFreezes()
    {
        Assert.That(FreezeCatchUp.IsFreeze(1f / 60f, 1f / 60f), Is.False);
        // fps-drop's 6 fps with DV's cap: each frame loses well under the threshold.
        Assert.That(FreezeCatchUp.IsFreeze(1f / 6f, 1f / 12f), Is.False);
    }

    [Test]
    public void TheSetMoves_ByTheCarsMeanError()
    {
        var errors = new FreezeCatchUp();
        errors.Add(30.6f);
        errors.Add(30.9f);
        errors.Add(31.2f);
        Assert.That(errors.TryGetDistance(out float metres), Is.True);
        Assert.That(metres, Is.EqualTo(30.9f).Within(0.001f));
        Assert.That(FreezeCatchUp.IsWorthMoving(metres), Is.True);
    }

    [Test]
    public void CarsThatDisagree_AreNotMoved()
    {
        // One car metres off from the rest: ours isn't the host's set.
        var errors = new FreezeCatchUp();
        errors.Add(30f);
        errors.Add(25f);
        Assert.That(errors.TryGetDistance(out _), Is.False);
    }

    [Test]
    public void NoKnownError_IsNotMoved()
    {
        Assert.That(new FreezeCatchUp().TryGetDistance(out _), Is.False);
    }

    [Test]
    public void ASmallGap_IsLeftToTheCorrection()
    {
        Assert.That(FreezeCatchUp.IsWorthMoving(1.5f), Is.False);
        Assert.That(FreezeCatchUp.IsWorthMoving(-2.5f), Is.True);
    }
}
