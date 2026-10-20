using System;

namespace Multiplayer.Core.Trains;

/// <summary>
/// How often the host sends a moving trainset's motion to one player, from how far the player is
/// from the set's nearest car. Every car stays on every client (dispatch maps show distant
/// trains), so a far set is still sent, only less often: every tick close by, a few times a
/// second further out, about once a second far away.
/// </summary>
public readonly struct TrainsetSendRate
{
    public TrainsetSendRate(float fullRateDistance, float midRateDistance, float midRateHz, float farRateHz, int tickRate)
    {
        FullRateDistance = Math.Max(0f, fullRateDistance);
        MidRateDistance = Math.Max(FullRateDistance, midRateDistance);
        MidIntervalTicks = IntervalFor(midRateHz, tickRate);
        FarIntervalTicks = Math.Max(MidIntervalTicks, IntervalFor(farRateHz, tickRate));
    }

    public float FullRateDistance { get; }
    public float MidRateDistance { get; }
    public byte MidIntervalTicks { get; }
    public byte FarIntervalTicks { get; }

    /// <summary>Ticks between samples for a player <paramref name="distance"/> metres from the set's nearest car.</summary>
    public byte IntervalTicks(float distance)
    {
        if (!(distance > FullRateDistance))   // NaN (no position) counts as close
            return 1;
        return distance <= MidRateDistance ? MidIntervalTicks : FarIntervalTicks;
    }

    /// <summary>
    /// Whether a set is due a sample to a player with this interval, <paramref name="ticksSinceSent"/>
    /// tick numbers after the last one it sent them. Tick numbers, not tick handler calls: the
    /// tick runs at most once a frame and skips numbers below 24 fps, so counting calls (and
    /// scheduling on multiples, which skipped numbers miss) sent a slow host's far trains every
    /// 2-3 s instead of every second (train-rate, 2026-10-20). Sets that started moving at
    /// different ticks stay spread over the interval.
    /// </summary>
    public static bool IsDue(uint ticksSinceSent, byte intervalTicks) =>
        intervalTicks <= 1 || ticksSinceSent >= intervalTicks;

    private static byte IntervalFor(float hz, int tickRate)
    {
        if (!(hz > 0f))
            return byte.MaxValue;
        return (byte)Math.Min(byte.MaxValue, Math.Max(1, (int)Math.Round(tickRate / hz)));
    }
}
