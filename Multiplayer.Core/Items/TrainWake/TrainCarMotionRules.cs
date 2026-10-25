namespace Multiplayer.Core.Items.TrainWake;

/// <summary>Why a car's motion wakes the items settled in it.</summary>
public enum CarMotionWake : byte
{
    None,
    HeavyAcceleration,
    HeavyBraking,
    TrainJolt,
    ViolentRotation,
    Derailment
}

/// <summary>A vector in a car's frame (Core has no Unity types).</summary>
public readonly struct MotionVector
{
    public MotionVector(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static readonly MotionVector Zero = default;
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public float Length() => (float)System.Math.Sqrt(X * X + Y * Y + Z * Z);
    public static float Dot(MotionVector a, MotionVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static MotionVector operator -(MotionVector a, MotionVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static MotionVector operator /(MotionVector a, float d) => new(a.X / d, a.Y / d, a.Z / d);
}

/// <summary>A car's motion at its last sample, in the car's own frame.</summary>
public readonly struct CarMotionSample
{
    public CarMotionSample(MotionVector velocity, MotionVector acceleration, MotionVector angularVelocity, bool derailed)
    {
        Velocity = velocity;
        Acceleration = acceleration;
        AngularVelocity = angularVelocity;
        Derailed = derailed;
    }

    public MotionVector Velocity { get; }
    public MotionVector Acceleration { get; }
    public MotionVector AngularVelocity { get; }
    public bool Derailed { get; }
}

/// <summary>
/// When a car's motion wakes its settled items (docs/design/items/train-wake.md, "Car motion").
/// Motion is measured in the car's frame (OI-PHY-12): a car taking a curve at a steady speed has
/// the same velocity in its own frame throughout, so the curve's centripetal acceleration, which
/// the items ride with the car, counts for nothing. In world space it did: 3.5 m/s² is 30 m/s on a
/// 260 m radius, and every settled item on a fast train woke on every such curve.
/// </summary>
public static class TrainCarMotionRules
{
    public const float HeavyAccelerationThreshold = 3.5f;
    public const float HeavyBrakingThreshold = 3f;
    public const float JoltThreshold = 14f;
    public const float JoltMinimumAcceleration = 1.5f;
    public const float AngularAccelerationThreshold = 1.25f;
    /// <summary>Braking only counts on a car moving faster than this, m/s.</summary>
    public const float MovingSpeed = 1f;

    /// <summary>
    /// What the car's motion since <paramref name="previous"/> wakes, from its velocity and angular
    /// velocity now, both in its own frame, <paramref name="dt"/> seconds later. Its acceleration in
    /// its own frame is the next sample's.
    /// </summary>
    public static CarMotionWake Classify(CarMotionSample previous, MotionVector velocity, MotionVector angularVelocity,
        bool derailed, float dt, out MotionVector acceleration)
    {
        acceleration = MotionVector.Zero;
        if (dt <= 0f)
            return CarMotionWake.None;
        acceleration = (velocity - previous.Velocity) / dt;
        MotionVector jolt = (acceleration - previous.Acceleration) / dt;
        MotionVector angularAcceleration = (angularVelocity - previous.AngularVelocity) / dt;
        float speed = previous.Velocity.Length();
        if (derailed && !previous.Derailed)
            return CarMotionWake.Derailment;
        if (angularAcceleration.Length() >= AngularAccelerationThreshold)
            return CarMotionWake.ViolentRotation;
        if (jolt.Length() >= JoltThreshold && acceleration.Length() >= JoltMinimumAcceleration)
            return CarMotionWake.TrainJolt;
        if (acceleration.Length() >= HeavyAccelerationThreshold)
            return MotionVector.Dot(acceleration, previous.Velocity) < 0f && speed > MovingSpeed
                ? CarMotionWake.HeavyBraking
                : CarMotionWake.HeavyAcceleration;
        if (speed > MovingSpeed && MotionVector.Dot(acceleration, previous.Velocity / speed) <= -HeavyBrakingThreshold)
            return CarMotionWake.HeavyBraking;
        return CarMotionWake.None;
    }
}
