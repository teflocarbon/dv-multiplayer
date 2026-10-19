using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Trains;

/// <summary>How a port's values are played out between the host's samples.</summary>
public enum PlayoutBlend
{
    /// <summary>A quantity (pressure, RPM, current): always interpolated.</summary>
    Continuous,
    /// <summary>A value that may be continuous or may jump (a normalised flow, a position, a
    /// decoded lever): interpolated, but a change of more than <see cref="PlayoutTrack.JumpLimit"/>
    /// in one sample is a step, so a crank's wrap from 1 to 0 doesn't sweep back through the turn.</summary>
    Guarded,
    /// <summary>A state: held, changing at the sample's tick.</summary>
    Step,
}

/// <summary>
/// One port's recent values from the host, stamped with its ticks, sampled at a time a little
/// behind the newest. The host sends a port only when it moved more than a small amount, so a
/// gap between samples can be a port that sat still and then moved: past
/// <see cref="MaxInterpolationTicks"/> the older value holds until a tick before the newer one.
/// </summary>
public sealed class PlayoutTrack
{
    public const float JumpLimit = 0.5f;
    public const double MaxInterpolationTicks = 4;
    // Samples kept: enough to cover the longest delay at the tick rate, with room.
    private const int CAPACITY = 16;

    private readonly struct Sample
    {
        public readonly double Tick;
        public readonly float Value;
        // Reached as a step from the sample before it, not a blend.
        public readonly bool Step;

        public Sample(double tick, float value, bool step)
        {
            Tick = tick;
            Value = value;
            Step = step;
        }
    }

    private readonly List<Sample> samples = new(CAPACITY);
    private readonly PlayoutBlend blend;

    public PlayoutTrack(PlayoutBlend blend)
    {
        this.blend = blend;
    }

    public bool IsEmpty => samples.Count == 0;

    /// <summary>The host's newest value.</summary>
    public float Latest => samples.Count == 0 ? 0f : samples[samples.Count - 1].Value;

    public double LatestTick => samples.Count == 0 ? 0 : samples[samples.Count - 1].Tick;

    public void Clear() => samples.Clear();

    /// <summary>Forgets the history and holds <paramref name="value"/> from <paramref name="tick"/>
    /// (a resync, or the value a client's own prediction left the port at).</summary>
    public void Reset(double tick, float value)
    {
        samples.Clear();
        samples.Add(new Sample(tick, value, step: true));
    }

    public void Add(uint hostTick, float value)
    {
        double tick = hostTick;
        if (samples.Count > 0)
        {
            Sample last = samples[samples.Count - 1];
            if (tick < last.Tick)
                return;
            if (tick == last.Tick)
            {
                samples[samples.Count - 1] = new Sample(tick, value, last.Step);
                return;
            }
            if (tick - last.Tick > MaxInterpolationTicks && value != last.Value)
                Append(new Sample(tick - 1, last.Value, step: true));
        }

        bool step = blend == PlayoutBlend.Step ||
                    (blend == PlayoutBlend.Guarded && samples.Count > 0 &&
                     Math.Abs(value - samples[samples.Count - 1].Value) > JumpLimit);
        Append(new Sample(tick, value, step));
    }

    private void Append(Sample sample)
    {
        if (samples.Count == CAPACITY)
            samples.RemoveAt(0);
        samples.Add(sample);
    }

    /// <summary>The value at <paramref name="tick"/>: held before the first sample and after the last.
    /// Time only moves on, so samples before the one shown are dropped. A step passed over entirely
    /// since the last call (a one-tick pulse, like a chuff, on a slow frame) goes to
    /// <paramref name="passedStep"/> first, so whatever listens to the port still sees it.</summary>
    public float ValueAt(double tick, Action<float> passedStep = null)
    {
        if (samples.Count == 0)
            return 0f;
        bool shownBefore = true;
        while (samples.Count > 1 && samples[1].Tick <= tick)
        {
            // The first one dropped is the one shown last time; any after it were never shown.
            if (!shownBefore && samples[0].Step)
                passedStep?.Invoke(samples[0].Value);
            shownBefore = false;
            samples.RemoveAt(0);
        }

        Sample first = samples[0];
        if (tick <= first.Tick || samples.Count == 1)
            return first.Value;
        Sample next = samples[1];
        if (next.Step)
            return first.Value;
        float t = (float)((tick - first.Tick) / (next.Tick - first.Tick));
        return first.Value + (next.Value - first.Value) * t;
    }

    /// <summary>Whether nothing is left to play out after <paramref name="tick"/>: the port holds
    /// the host's newest value.</summary>
    public bool SettledAt(double tick) => samples.Count == 0 || tick >= samples[samples.Count - 1].Tick;
}
