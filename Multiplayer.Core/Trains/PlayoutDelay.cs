using System;

namespace Multiplayer.Core.Trains;

/// <summary>
/// How far behind the host's newest values a car's ports are shown, moved gradually so the values
/// never jump back in time: zero while the local player is driving the car (they're never
/// delayed), an observer's delay otherwise. Growing it slows the car's values down for a moment;
/// shrinking it, which happens when the player takes a control, catches up faster.
/// </summary>
public sealed class PlayoutDelay
{
    // Seconds of delay gained per second: values play at 75 % speed while it grows.
    public const double GROW_PER_SECOND = 0.25;
    // Seconds of delay dropped per second: values play at double speed while it shrinks.
    public const double SHRINK_PER_SECOND = 1.0;

    public double Seconds { get; private set; }

    public void Step(double targetSeconds, double deltaSeconds)
    {
        if (deltaSeconds <= 0)
            return;
        if (targetSeconds > Seconds)
            Seconds = Math.Min(targetSeconds, Seconds + GROW_PER_SECOND * deltaSeconds);
        else
            Seconds = Math.Max(targetSeconds, Seconds - SHRINK_PER_SECOND * deltaSeconds);
    }
}
