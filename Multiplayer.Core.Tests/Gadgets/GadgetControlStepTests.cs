using Multiplayer.Core.Gadgets;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Gadgets;

[TestFixture]
public sealed class GadgetControlStepTests
{
    [Test]
    public void ASteppedLever_MovesOneOfItsNotchGaps() =>
        Assert.That(GadgetControlStep.Lever(1f, stepped: true, notches: 11, 0f, 90f), Is.EqualTo(0.1f).Within(1e-6f));

    [Test]
    public void AFreeLever_MovesItsScrollInDegreesOverItsRange() =>
        Assert.That(GadgetControlStep.Lever(4.5f, stepped: false, notches: 0, -45f, 45f), Is.EqualTo(0.05f).Within(1e-6f));

    [Test]
    public void AnInvertedLever_StepsTheSameDistance() =>
        Assert.That(GadgetControlStep.Lever(-4.5f, stepped: false, notches: 0, -45f, 45f), Is.EqualTo(0.05f).Within(1e-6f));

    [Test]
    public void ALeverWithNoRange_FallsBack() =>
        Assert.That(GadgetControlStep.Lever(4.5f, stepped: false, notches: 0, 10f, 10f), Is.EqualTo(GadgetControlStep.Fallback));

    [Test]
    public void APuller_MovesItsScroll() =>
        Assert.That(GadgetControlStep.Puller(-0.025f), Is.EqualTo(0.025f).Within(1e-6f));

    [Test]
    public void NoScroll_FallsBack()
    {
        Assert.That(GadgetControlStep.Puller(0f), Is.EqualTo(GadgetControlStep.Fallback));
        Assert.That(GadgetControlStep.Lever(0f, stepped: true, notches: 20, 0f, 90f), Is.EqualTo(GadgetControlStep.Fallback));
    }

    [Test]
    public void AStepIsNeverMoreThanTheWholeRange() =>
        Assert.That(GadgetControlStep.Lever(200f, stepped: false, notches: 0, 0f, 90f), Is.EqualTo(1f));
}
