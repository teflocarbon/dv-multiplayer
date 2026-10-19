using Multiplayer.Core.Trains;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Trains;

[TestFixture]
public sealed class PortPlayoutTests
{
    private const double Tick = 1.0 / 24;
    private const double MinDelay = 2 * Tick;
    private const double MaxDelay = 0.3;

    private static PlayoutClock Clock() => new(Tick, MinDelay, MaxDelay);

    [Test]
    public void Clock_EvenArrivals_UsesTheMinimumDelay()
    {
        PlayoutClock clock = Clock();
        for (uint t = 100; t < 200; t++)
            clock.OnArrival(t, 10 + t * Tick);
        Assert.That(clock.JitterSeconds, Is.EqualTo(0).Within(1e-9));
        Assert.That(clock.TargetDelaySeconds, Is.EqualTo(MinDelay).Within(1e-9));
        // The tick arriving now is the newest one.
        Assert.That(clock.HostTickAt(10 + 199 * Tick), Is.EqualTo(199).Within(1e-6));
    }

    [Test]
    public void Clock_Jitter_GrowsTheDelayByIt()
    {
        PlayoutClock clock = Clock();
        // Every fourth arrival is 80 ms late.
        for (uint t = 100; t < 200; t++)
            clock.OnArrival(t, 10 + t * Tick + (t % 4 == 0 ? 0.08 : 0));
        Assert.That(clock.JitterSeconds, Is.EqualTo(0.08).Within(0.01));
        Assert.That(clock.TargetDelaySeconds, Is.EqualTo(Tick + 0.08 * 1.15).Within(0.01));
    }

    [Test]
    public void Clock_DelayIsCapped()
    {
        PlayoutClock clock = Clock();
        for (uint t = 100; t < 200; t++)
            clock.OnArrival(t, 10 + t * Tick + (t % 2 == 0 ? 0.6 : 0));
        Assert.That(clock.TargetDelaySeconds, Is.EqualTo(MaxDelay).Within(1e-9));
    }

    [Test]
    public void Clock_JitterDecaysOnceTheLinkSettles()
    {
        PlayoutClock clock = Clock();
        clock.OnArrival(100, 10 + 100 * Tick);
        clock.OnArrival(101, 10 + 101 * Tick + 0.1);
        for (uint t = 102; t < 102 + 24 * 12; t++)
            clock.OnArrival(t, 10 + t * Tick);
        Assert.That(clock.JitterSeconds, Is.LessThan(0.01));
    }

    [Test]
    public void Clock_NewTimeline_StartsOver()
    {
        PlayoutClock clock = Clock();
        for (uint t = 1000; t < 1100; t++)
            clock.OnArrival(t, 10 + t * Tick);
        // The host restarted: its ticks start again from zero.
        clock.OnArrival(5, 60);
        Assert.That(clock.JitterSeconds, Is.EqualTo(0));
        Assert.That(clock.HostTickAt(60), Is.EqualTo(5).Within(1e-6));
    }

    [Test]
    public void Track_InterpolatesBetweenTicks()
    {
        PlayoutTrack track = new(PlayoutBlend.Continuous);
        track.Add(10, 0f);
        track.Add(11, 1f);
        Assert.That(track.ValueAt(10.25), Is.EqualTo(0.25f).Within(1e-5));
        Assert.That(track.ValueAt(11.5), Is.EqualTo(1f));
        Assert.That(track.SettledAt(11.5), Is.True);
    }

    [Test]
    public void Track_BunchedArrivals_StillPlayEvenly()
    {
        // Ticks 11 and 12 arrived together; the stamps space them out again.
        PlayoutTrack track = new(PlayoutBlend.Continuous);
        track.Add(10, 0f);
        track.Add(11, 0.1f);
        track.Add(12, 0.2f);
        Assert.That(track.ValueAt(10.5), Is.EqualTo(0.05f).Within(1e-5));
        Assert.That(track.ValueAt(11.5), Is.EqualTo(0.15f).Within(1e-5));
    }

    [Test]
    public void Track_AfterALongGap_HoldsUntilTheTickBefore()
    {
        // Sat at 2 bar, then the host sent 3 bar twenty ticks later: it moved just before.
        PlayoutTrack track = new(PlayoutBlend.Continuous);
        track.Add(10, 2f);
        track.Add(30, 3f);
        Assert.That(track.ValueAt(20), Is.EqualTo(2f));
        Assert.That(track.ValueAt(29.5), Is.EqualTo(2.5f).Within(1e-5));
    }

    [Test]
    public void Track_ShortGap_Interpolates()
    {
        // A slowly rising pressure, sent every third tick.
        PlayoutTrack track = new(PlayoutBlend.Continuous);
        track.Add(10, 2f);
        track.Add(13, 2.003f);
        Assert.That(track.ValueAt(11.5), Is.EqualTo(2.0015f).Within(1e-5));
    }

    [Test]
    public void Track_Guarded_StepsOnAJump()
    {
        // A crank's rotation wrapping from the end of a turn to the start.
        PlayoutTrack track = new(PlayoutBlend.Guarded);
        track.Add(10, 0.9f);
        track.Add(11, 0.1f);
        Assert.That(track.ValueAt(10.5), Is.EqualTo(0.9f));
        Assert.That(track.ValueAt(11), Is.EqualTo(0.1f));
    }

    [Test]
    public void Track_Guarded_InterpolatesSmallChanges()
    {
        PlayoutTrack track = new(PlayoutBlend.Guarded);
        track.Add(10, 0.2f);
        track.Add(11, 0.4f);
        Assert.That(track.ValueAt(10.5), Is.EqualTo(0.3f).Within(1e-5));
    }

    [Test]
    public void Track_Step_HoldsUntilTheTick()
    {
        PlayoutTrack track = new(PlayoutBlend.Step);
        track.Add(10, 0f);
        track.Add(11, 1f);
        Assert.That(track.ValueAt(10.9), Is.EqualTo(0f));
        Assert.That(track.ValueAt(11), Is.EqualTo(1f));
    }

    [Test]
    public void Track_PulsePassedInOneFrame_IsStillSeen()
    {
        // A chuff: 0, then 1 for a tick, then 0; the frame jumps from before it to after it.
        PlayoutTrack track = new(PlayoutBlend.Guarded);
        track.Add(10, 0f);
        track.Add(11, 1f);
        track.Add(12, 0f);
        var seen = new System.Collections.Generic.List<float>();
        Assert.That(track.ValueAt(10.5, seen.Add), Is.EqualTo(0f));
        Assert.That(seen, Is.Empty);
        Assert.That(track.ValueAt(12.5, seen.Add), Is.EqualTo(0f));
        Assert.That(seen, Is.EqualTo(new[] { 1f }));
    }

    [Test]
    public void Track_SameTickTwice_KeepsTheLater()
    {
        PlayoutTrack track = new(PlayoutBlend.Continuous);
        track.Add(10, 0f);
        track.Add(11, 1f);
        track.Add(11, 2f);
        Assert.That(track.Latest, Is.EqualTo(2f));
        Assert.That(track.ValueAt(10.5), Is.EqualTo(1f).Within(1e-5));
    }

    [Test]
    public void Track_OlderTick_IsIgnored()
    {
        PlayoutTrack track = new(PlayoutBlend.Continuous);
        track.Add(11, 1f);
        track.Add(10, 5f);
        Assert.That(track.Latest, Is.EqualTo(1f));
    }

    [Test]
    public void Track_BeforeTheFirstSample_HoldsIt()
    {
        PlayoutTrack track = new(PlayoutBlend.Continuous);
        track.Add(10, 3f);
        track.Add(11, 4f);
        Assert.That(track.ValueAt(5), Is.EqualTo(3f));
        Assert.That(track.SettledAt(5), Is.False);
    }

    [Test]
    public void Track_Reset_HoldsTheValue()
    {
        PlayoutTrack track = new(PlayoutBlend.Continuous);
        track.Add(10, 0f);
        track.Add(11, 1f);
        track.Reset(11, 0.5f);
        Assert.That(track.ValueAt(12), Is.EqualTo(0.5f));
        track.Add(12, 1f);
        Assert.That(track.ValueAt(11.5), Is.EqualTo(0.75f).Within(1e-5));
    }

    [Test]
    public void Delay_GrowsSlowlyAndShrinksFast()
    {
        PlayoutDelay delay = new();
        delay.Step(0.1, 0.2);
        Assert.That(delay.Seconds, Is.EqualTo(0.05).Within(1e-9));
        delay.Step(0.1, 1);
        Assert.That(delay.Seconds, Is.EqualTo(0.1).Within(1e-9));
        delay.Step(0, 0.05);
        Assert.That(delay.Seconds, Is.EqualTo(0.05).Within(1e-9));
        delay.Step(0, 1);
        Assert.That(delay.Seconds, Is.EqualTo(0));
    }

    [Test]
    public void ClockAndTrack_JitteryLink_PlayWithoutHolding()
    {
        // A whistle opening steadily, sent every tick, arriving with up to 60 ms of jitter: played
        // out at the clock's delay, every frame's value is on the ramp, none held at a stale one.
        PlayoutClock clock = Clock();
        PlayoutTrack track = new(PlayoutBlend.Guarded);
        var arrivals = new System.Collections.Generic.List<(double at, uint tick)>();
        System.Random random = new(1);
        for (uint t = 0; t < 240; t++)
            arrivals.Add((5 + t * Tick + random.NextDouble() * 0.06, t + 1000));
        arrivals.Sort((a, b) => a.at.CompareTo(b.at));

        int next = 0;
        double maxError = 0;
        for (double now = 5; now < 5 + 10; now += 1.0 / 90)
        {
            while (next < arrivals.Count && arrivals[next].at <= now)
            {
                clock.OnArrival(arrivals[next].tick, arrivals[next].at);
                track.Add(arrivals[next].tick, (arrivals[next].tick - 1000) / 240f);
                next++;
            }
            if (!clock.Started || now < 6)
                continue;
            double shown = clock.HostTickAt(now) - clock.TargetDelaySeconds / Tick;
            if (shown > 1000 + 239)
                break;
            float expected = (float)((shown - 1000) / 240);
            maxError = System.Math.Max(maxError, System.Math.Abs(track.ValueAt(shown) - expected));
        }
        // Under a tenth of one tick's change: at most a few milliseconds held, when a faster
        // arrival than any before moves the clock on.
        Assert.That(maxError, Is.LessThan(0.1 / 240));
    }
}
