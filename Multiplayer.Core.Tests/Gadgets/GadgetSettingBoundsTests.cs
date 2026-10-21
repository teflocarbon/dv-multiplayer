using Multiplayer.Core.Gadgets;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Gadgets;

[TestFixture]
public sealed class GadgetSettingBoundsTests
{
    [TestCase(0f)]
    [TestCase(3f)]
    [TestCase(7f)]
    [TestCase(6.9995f)]
    public void AStepWithinTheRange_IsAccepted(float value) =>
        Assert.That(GadgetSettingBounds.Steps(8).Refusal(value), Is.Null);

    [TestCase(-1f)]
    [TestCase(8f)]
    [TestCase(99f)]
    public void AStepOutsideTheRange_IsRefused(float value) =>
        Assert.That(GadgetSettingBounds.Steps(8).Refusal(value), Does.Contain("outside"));

    [Test]
    public void HalfAStep_IsRefused() =>
        Assert.That(GadgetSettingBounds.Steps(8).Refusal(2.5f), Does.Contain("whole"));

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void NotANumber_IsRefused(float value) =>
        Assert.That(new GadgetSettingBounds(0f, 1f, false).Refusal(value), Is.EqualTo("not a number"));

    [TestCase(0f)]
    [TestCase(0.37f)]
    [TestCase(1f)]
    public void AContinuousSetting_TakesAnyValueInRange(float value) =>
        Assert.That(new GadgetSettingBounds(0f, 1f, false).Refusal(value), Is.Null);

    [Test]
    public void AToggle_IsZeroOrOne()
    {
        Assert.That(GadgetSettingBounds.Toggle.Refusal(0f), Is.Null);
        Assert.That(GadgetSettingBounds.Toggle.Refusal(1f), Is.Null);
        Assert.That(GadgetSettingBounds.Toggle.Refusal(0.5f), Is.Not.Null);
        Assert.That(GadgetSettingBounds.Toggle.Refusal(2f), Is.Not.Null);
    }

    [Test]
    public void NoSteps_OnlyZero()
    {
        Assert.That(GadgetSettingBounds.Steps(0).Refusal(0f), Is.Null);
        Assert.That(GadgetSettingBounds.Steps(0).Refusal(1f), Is.Not.Null);
    }
}

[TestFixture]
public sealed class GadgetKnobLeaseTests
{
    [Test]
    public void TheSameGadget_HasTheSameIdInEveryGame() =>
        Assert.That(GadgetKnobLease.IdOf(812), Is.EqualTo(GadgetKnobLease.IdOf(812)));

    [Test]
    public void EveryGadget_HasItsOwnId()
    {
        var seen = new System.Collections.Generic.HashSet<uint>();
        for (int netId = 1; netId <= ushort.MaxValue; netId++)
            Assert.That(seen.Add(GadgetKnobLease.IdOf((ushort)netId)), Is.True, $"item {netId}");
    }

    [Test]
    public void ItIsTheFnv1aHashOfThePrefixedNetId()
    {
        // FNV-1a of "gadget/7", as the car's port ids hash their port's id.
        uint hash = 0x811C9DC5;
        foreach (char c in "gadget/7")
            hash = unchecked((hash ^ c) * 0x01000193);
        Assert.That(GadgetKnobLease.IdOf(7), Is.EqualTo(hash));
    }
}
