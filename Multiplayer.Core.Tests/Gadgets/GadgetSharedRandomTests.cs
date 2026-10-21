using Multiplayer.Core.Gadgets;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Gadgets;

[TestFixture]
public sealed class GadgetSharedRandomTests
{
    [Test]
    public void TheSameStepAndGadget_RollTheSameEverywhere() =>
        Assert.That(GadgetSharedRandom.Unit(123456, 42), Is.EqualTo(GadgetSharedRandom.Unit(123456, 42)));

    [Test]
    public void EveryRoll_IsInZeroToOne()
    {
        for (long step = 0; step < 20000; step++)
        {
            float roll = GadgetSharedRandom.Unit(step, (int)(step % 97) - 40);
            Assert.That(roll, Is.GreaterThanOrEqualTo(0f).And.LessThan(1f));
        }
    }

    [Test]
    public void AChanceOfAHalf_FaultsAboutHalfTheSteps()
    {
        int hits = 0;
        const int steps = 20000;
        for (long step = 0; step < steps; step++)
            if (GadgetSharedRandom.Unit(step, 7) < 0.5f)
                hits++;
        Assert.That(hits / (double)steps, Is.EqualTo(0.5).Within(0.02));
    }

    [Test]
    public void TwoGadgets_RollDifferently()
    {
        int same = 0;
        for (long step = 0; step < 1000; step++)
            if ((GadgetSharedRandom.Unit(step, 1) < 0.5f) == (GadgetSharedRandom.Unit(step, 2) < 0.5f))
                same++;
        Assert.That(same, Is.InRange(400, 600));
    }

    [TestCase(0.512f, 0.5f)]
    [TestCase(0.526f, 0.55f)]
    [TestCase(0f, 0f)]
    [TestCase(1f, 1f)]
    public void Coarse_RoundsToTheGrain(float chance, float expected) =>
        Assert.That(GadgetSharedRandom.Coarse(chance), Is.EqualTo(expected).Within(1e-5f));
}
