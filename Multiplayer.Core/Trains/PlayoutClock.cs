using System;

namespace Multiplayer.Core.Trains;

/// <summary>
/// Where on the host's tick timeline a client shows the host's port values, and how far behind
/// the newest ones it should stay to play them out evenly.
///
/// Port values leave the host stamped with its tick, 24 a second, and land unevenly: a link's
/// jitter, and on a reliable channel a lost packet's resend, bunch them up and leave gaps. Each
/// arrival gives the offset between the client's clock and the host's ticks (latency plus the
/// clock's own start). The lowest recent offset is the latency with no jitter; how far arrivals
/// run above it is the jitter, and the delay covers it, plus one tick so the value after the one
/// being shown has arrived by the time it's needed.
/// </summary>
public sealed class PlayoutClock
{
    private readonly double tickSeconds;
    private readonly double minDelaySeconds;
    private readonly double maxDelaySeconds;

    // The lowest offset rises this fast (seconds per second) between lower arrivals, so it follows
    // a latency that grows, or the host's ticks falling behind its clock, instead of keeping the
    // lowest one seen.
    private const double FLOOR_RISE_PER_SECOND = 0.01;
    // The jitter estimate holds the highest recent excess and decays this fast (seconds per second):
    // slowly, since a link's worst arrivals recur, and each one the delay doesn't cover is a hold.
    private const double JITTER_DECAY_PER_SECOND = 0.01;
    // Headroom over the jitter seen, for arrivals a little later than any so far.
    private const double JITTER_MARGIN = 1.15;
    // An offset this far from the floor either way is a new timeline (the host restarted its ticks,
    // or the client stalled for long): start over rather than count it as jitter.
    private const double RESET_SECONDS = 2.0;

    private bool started;
    private double floor;
    private double jitter;
    private double lastArrival;

    public PlayoutClock(double tickSeconds, double minDelaySeconds, double maxDelaySeconds)
    {
        this.tickSeconds = tickSeconds;
        this.minDelaySeconds = minDelaySeconds;
        this.maxDelaySeconds = maxDelaySeconds;
    }

    public bool Started => started;

    /// <summary>The estimated jitter on arrivals, in seconds.</summary>
    public double JitterSeconds => jitter;

    /// <summary>The delay an observer plays out at: one tick plus the jitter, within the limits.</summary>
    public double TargetDelaySeconds => Math.Min(maxDelaySeconds, Math.Max(minDelaySeconds, tickSeconds + jitter * JITTER_MARGIN));

    /// <summary>Records values stamped <paramref name="hostTick"/> arriving at <paramref name="arrivalSeconds"/>
    /// on the client's clock.</summary>
    public void OnArrival(uint hostTick, double arrivalSeconds)
    {
        double offset = arrivalSeconds - hostTick * tickSeconds;
        if (!started || Math.Abs(offset - floor) > RESET_SECONDS + maxDelaySeconds)
        {
            started = true;
            floor = offset;
            jitter = 0;
            lastArrival = arrivalSeconds;
            return;
        }

        double elapsed = Math.Max(0, arrivalSeconds - lastArrival);
        lastArrival = arrivalSeconds;
        floor = Math.Min(floor + FLOOR_RISE_PER_SECOND * elapsed, offset);
        jitter = Math.Max(offset - floor, jitter - JITTER_DECAY_PER_SECOND * elapsed);
        if (jitter < 0)
            jitter = 0;
    }

    /// <summary>The host's tick (fractional) that is arriving now with no jitter.</summary>
    public double HostTickAt(double nowSeconds) => started ? (nowSeconds - floor) / tickSeconds : 0;
}
