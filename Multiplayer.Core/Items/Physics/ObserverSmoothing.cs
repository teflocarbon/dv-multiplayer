using System;

namespace Multiplayer.Core.Items.Physics;

/// <summary>
/// How a game that doesn't simulate a leased item shows its flight (docs/design/items/physics.md,
/// OI-PHY-10): it extrapolates the last accepted sample by its velocity for the sample's age, at most
/// <see cref="PredictionLimit"/> seconds, and nothing for a sleeping or settled item; then it moves
/// the item towards that target at <see cref="InterpolationRate"/> per second, or puts it there at
/// once from <see cref="SnapDistance"/> metres away.
/// </summary>
public static class ObserverSmoothing
{
    public const float SnapDistance = 6f;
    public const float InterpolationRate = 10f;
    public const float PredictionLimit = 0.15f;

    /// <summary>Seconds to extrapolate a sample <paramref name="sampleAge"/> seconds old.</summary>
    public static float PredictionSeconds(float sampleAge, bool sleepingOrSettled) =>
        sleepingOrSettled ? 0f : Math.Min(Math.Max(sampleAge, 0f), PredictionLimit);

    /// <summary>Whether the item is put on its target at once rather than moved towards it.</summary>
    public static bool Snaps(float distance, bool immediate) => immediate || distance >= SnapDistance;

    /// <summary>How far towards its target an item moves in a frame of <paramref name="deltaTime"/> seconds (0 to 1).</summary>
    public static float Blend(float deltaTime) => 1f - (float)Math.Exp(-InterpolationRate * deltaTime);
}
