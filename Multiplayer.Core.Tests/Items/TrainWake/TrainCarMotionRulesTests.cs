using System;
using System.Numerics;
using Multiplayer.Core.Items.TrainWake;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.TrainWake;

[TestFixture]
public sealed class TrainCarMotionRulesTests
{
    private const float Dt = 0.1f;

    // A car at a heading (radians about Y), moving forward (+Z in its own frame) at a speed.
    private static Quaternion Heading(float yaw) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
    private static Vector3 World(float yaw, float speed) => Vector3.Transform(new Vector3(0, 0, speed), Heading(yaw));

    // What the server does with the car's rigidbody: its world-space velocity in its own frame.
    private static MotionVector InCarFrame(float yaw, Vector3 world)
    {
        Vector3 local = Vector3.Transform(world, Quaternion.Inverse(Heading(yaw)));
        return new MotionVector(local.X, local.Y, local.Z);
    }

    private static MotionVector V(float x, float y, float z) => new(x, y, z);

    // Samples a car's motion along a path and classifies each step as the server does.
    private static CarMotionWake Drive(Func<int, (float Yaw, float Speed)> at, int steps)
    {
        (float yaw0, float speed0) = at(0);
        CarMotionSample previous = new(InCarFrame(yaw0, World(yaw0, speed0)), MotionVector.Zero, MotionVector.Zero, false);
        float lastYaw = yaw0;
        for (int i = 1; i <= steps; i++)
        {
            (float yaw, float speed) = at(i);
            MotionVector angular = InCarFrame(yaw, new Vector3(0, (yaw - lastYaw) / Dt, 0));
            MotionVector velocity = InCarFrame(yaw, World(yaw, speed));
            CarMotionWake wake = TrainCarMotionRules.Classify(previous, velocity, angular, false, Dt, out MotionVector acceleration);
            // The first step starts the motion from rest: its change of rate is the sampler's start.
            if (i > 1 && wake != CarMotionWake.None)
                return wake;
            previous = new CarMotionSample(velocity, acceleration, angular, false);
            lastYaw = yaw;
        }
        return CarMotionWake.None;
    }

    [Test]
    public void ASteadyCurveWakesNothing()
    {
        // 30 m/s on a 200 m radius: 4.5 m/s² of centripetal acceleration, which world space counted.
        float rate = 30f / 200f;
        Assert.That(Drive(i => (rate * Dt * i, 30f), 40), Is.EqualTo(CarMotionWake.None));
        Assert.That((World(rate * Dt, 30f) - World(0, 30f)).Length() / Dt, Is.GreaterThan(TrainCarMotionRules.HeavyAccelerationThreshold),
            "the same curve, measured in world space");
    }

    [Test]
    public void HardBrakingOnACurveStillDoes()
    {
        // 6 m/s² of braking from 20 m/s, on a 200 m radius.
        float rate = 20f / 200f;
        Assert.That(Drive(i => (rate * Dt * i, Math.Max(0f, 20f - 0.6f * i)), 20), Is.EqualTo(CarMotionWake.HeavyBraking));
    }

    [Test]
    public void HardAccelerationDoes() =>
        Assert.That(Drive(i => (0f, 5f + 0.4f * i), 20), Is.EqualTo(CarMotionWake.HeavyAcceleration));

    [Test]
    public void ADerailmentDoes()
    {
        CarMotionSample previous = new(V(0, 0, 10), MotionVector.Zero, MotionVector.Zero, false);
        Assert.That(TrainCarMotionRules.Classify(previous, V(0, 0, 10), MotionVector.Zero, true, Dt, out _),
            Is.EqualTo(CarMotionWake.Derailment));
    }

    [Test]
    public void AJoltDoes()
    {
        // From steady to 2 m/s² in a tenth of a second: 20 m/s³.
        CarMotionSample previous = new(V(0, 0, 10), MotionVector.Zero, MotionVector.Zero, false);
        Assert.That(TrainCarMotionRules.Classify(previous, V(0, 0, 10.2f), MotionVector.Zero, false, Dt, out _),
            Is.EqualTo(CarMotionWake.TrainJolt));
    }

    [Test]
    public void NoTimeIsNoMotion() =>
        Assert.That(TrainCarMotionRules.Classify(default, V(0, 0, 50), MotionVector.Zero, false, 0f, out _),
            Is.EqualTo(CarMotionWake.None));
}
