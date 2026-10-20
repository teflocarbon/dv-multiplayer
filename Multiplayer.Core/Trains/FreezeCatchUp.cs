using System;

namespace Multiplayer.Core.Trains;

/// <summary>
/// Catching a client's moving trainset up with the host's after the client froze. While a frame
/// stalls (loading after a teleport, a hitch) the client's game time barely moves (DV caps a
/// frame's delta) and its trains with it, while the host's run on: a 3.25 s freeze at 9.4 m/s
/// left the client's train 31 m behind. The ordinary correction closes a gap at a few m/s, so the
/// 2 s full sync snapped the rest in view a second later. Right after the freeze the screen has
/// just jumped anyway, so the set is moved there at once, as one: the cars' errors are averaged
/// (they differ only by coupler slack) and every car is moved by it.
/// </summary>
public struct FreezeCatchUp
{
    /// <summary>Seconds of game time one frame may fall behind real time before it counts as a freeze
    /// (a hitch a player notices; at 6 fps a frame loses under 0.1 s).</summary>
    public const float FreezeSeconds = 0.25f;

    /// <summary>Real seconds after a freeze in which a set is caught up: its fresh samples arrive
    /// a frame or two later.</summary>
    public const float WindowSeconds = 1f;

    /// <summary>Metres off below which the ordinary correction is left to close the gap.</summary>
    public const float MinDistance = 2f;

    /// <summary>Metres the cars' errors may differ by: more means our set isn't the host's.</summary>
    public const float MaxSpread = 1f;

    private float sum, min, max;
    private int count;

    /// <summary>Whether a frame (or a stretch without physics) that took <paramref name="realSeconds"/>
    /// but advanced game time only <paramref name="gameSeconds"/> was a freeze.</summary>
    public static bool IsFreeze(float realSeconds, float gameSeconds) => realSeconds - gameSeconds > FreezeSeconds;

    /// <summary>Adds one car's along-track error (metres the host's car is ahead of ours).</summary>
    public void Add(float error)
    {
        min = count == 0 ? error : Math.Min(min, error);
        max = count == 0 ? error : Math.Max(max, error);
        sum += error;
        count++;
    }

    /// <summary>The distance to move every car of the set; false when none is known or they disagree.</summary>
    public bool TryGetDistance(out float metres)
    {
        metres = count > 0 ? sum / count : 0f;
        return count > 0 && max - min <= MaxSpread;
    }

    /// <summary>Whether <paramref name="metres"/> is worth moving the set for.</summary>
    public static bool IsWorthMoving(float metres) => Math.Abs(metres) >= MinDistance;
}
