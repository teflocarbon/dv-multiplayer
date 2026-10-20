using Multiplayer.Core.Trains;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Trains;

[TestFixture]
public sealed class TrainsetSendRateTests
{
    private static readonly TrainsetSendRate Defaults = new(1500f, 4000f, 4f, 1f, 24);

    [Test]
    public void EveryTick_WithinTheFullRateDistance()
    {
        Assert.That(Defaults.IntervalTicks(0f), Is.EqualTo(1));
        Assert.That(Defaults.IntervalTicks(1500f), Is.EqualTo(1));
    }

    [Test]
    public void FewTimesASecond_FurtherOut()
    {
        Assert.That(Defaults.IntervalTicks(1500.1f), Is.EqualTo(6));
        Assert.That(Defaults.IntervalTicks(4000f), Is.EqualTo(6));
    }

    [Test]
    public void OnceASecond_FarAway()
    {
        Assert.That(Defaults.IntervalTicks(4000.1f), Is.EqualTo(24));
        Assert.That(Defaults.IntervalTicks(20000f), Is.EqualTo(24));
    }

    [Test]
    public void NoPosition_CountsAsClose()
    {
        Assert.That(Defaults.IntervalTicks(float.NaN), Is.EqualTo(1));
    }

    [Test]
    public void Settings_AreKeptInOrder()
    {
        // A mid distance under the full one, or a far rate above the mid one, can't make a
        // further player get more samples than a nearer one.
        var odd = new TrainsetSendRate(2000f, 1000f, 2f, 12f, 24);
        Assert.That(odd.MidRateDistance, Is.EqualTo(2000f));
        Assert.That(odd.FarIntervalTicks, Is.EqualTo(odd.MidIntervalTicks));
        Assert.That(odd.IntervalTicks(1999f), Is.EqualTo(1));
        Assert.That(odd.IntervalTicks(2001f), Is.EqualTo(12));
    }

    [Test]
    public void Rates_AreClampedToTheTickRate()
    {
        var fast = new TrainsetSendRate(100f, 200f, 100f, 0f, 24);
        Assert.That(fast.MidIntervalTicks, Is.EqualTo(1));
        Assert.That(fast.FarIntervalTicks, Is.EqualTo(byte.MaxValue));
    }

    [Test]
    public void Due_OnceTheIntervalHasPassed()
    {
        Assert.That(TrainsetSendRate.IsDue(23, 24), Is.False);
        Assert.That(TrainsetSendRate.IsDue(24, 24), Is.True);
        Assert.That(TrainsetSendRate.IsDue(0, 1), Is.True);
        Assert.That(TrainsetSendRate.IsDue(5, 6), Is.False);
        Assert.That(TrainsetSendRate.IsDue(6, 6), Is.True);
    }

    [Test]
    public void Due_AtTheRateInRealTime_WhenTheTickSkipsNumbers()
    {
        // A host at 10 fps runs the tick once a frame, advancing it 2 or 3 numbers each time.
        // Sending whenever the interval has passed keeps close to 1 Hz; the old multiples of 24
        // (and of call counts) were missed or ran slow.
        int[] steps = { 2, 3, 2, 3, 2 };
        uint tick = 0, sentAt = 0;
        int sends = 0;
        for (int i = 0; i < 240; i++)
        {
            tick += (uint)steps[i % steps.Length];
            if (TrainsetSendRate.IsDue(tick - sentAt, 24))
            {
                sends++;
                sentAt = tick;
            }
        }
        float seconds = tick / 24f;
        Assert.That(sends / seconds, Is.InRange(0.85f, 1f));
    }
}
