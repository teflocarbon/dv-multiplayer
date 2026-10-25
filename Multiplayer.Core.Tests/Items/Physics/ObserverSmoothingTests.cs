using System;
using Multiplayer.Core.Items.Physics;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Physics;

/// <summary>Observers extrapolate one target for up to 0.15 s and smooth towards it at 10/s (OI-PHY-10).</summary>
[TestFixture]
public sealed class ObserverSmoothingTests
{
    [TestCase(0.05f, 0.05f)]
    [TestCase(0.15f, 0.15f)]
    [TestCase(1f, 0.15f)]
    [TestCase(-0.2f, 0f)]
    public void AFlyingItemIsExtrapolatedForItsSamplesAgeUpTo015s(float age, float seconds) =>
        Assert.That(ObserverSmoothing.PredictionSeconds(age, sleepingOrSettled: false), Is.EqualTo(seconds).Within(1e-6f));

    [Test]
    public void ASleepingOrSettledItemIsntExtrapolated() =>
        Assert.That(ObserverSmoothing.PredictionSeconds(0.1f, sleepingOrSettled: true), Is.EqualTo(0f));

    [TestCase(5.99f, false)]
    [TestCase(6f, true)]
    [TestCase(40f, true)]
    public void AnItemSixMetresOrMoreFromItsTargetIsPutThere(float distance, bool snaps) =>
        Assert.That(ObserverSmoothing.Snaps(distance, immediate: false), Is.EqualTo(snaps));

    [Test]
    public void AnImmediatePoseIsPutThereFromAnyDistance() =>
        Assert.That(ObserverSmoothing.Snaps(0.01f, immediate: true), Is.True);

    [Test]
    public void ItMovesTowardsItsTargetAt10PerSecond()
    {
        Assert.That(ObserverSmoothing.Blend(0f), Is.EqualTo(0f));
        // A tenth of a second covers 1 - e^-1 of the way, whatever the frame rate.
        Assert.That(ObserverSmoothing.Blend(0.1f), Is.EqualTo(1f - (float)Math.Exp(-1)).Within(1e-6f));
        float tenFrames = 1f;
        for (int frame = 0; frame < 10; frame++)
            tenFrames *= 1f - ObserverSmoothing.Blend(0.01f);
        Assert.That(1f - tenFrames, Is.EqualTo(ObserverSmoothing.Blend(0.1f)).Within(1e-5f));
        Assert.That(ObserverSmoothing.Blend(5f), Is.GreaterThan(0.9999f).And.LessThanOrEqualTo(1f));
    }
}
