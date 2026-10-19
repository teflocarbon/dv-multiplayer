using System;
using Multiplayer.Core.Trains;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Trains;

[TestFixture]
public sealed class HandcarPumpersTests
{
    private const float MaxTorque = 1000f;

    // Straight line: the further the handle is ahead of the crank, the harder it pushes.
    private static readonly Func<float, float> Curve = diff => Math.Min(1f, diff * 2f);

    private static float Combined(HandcarPumpers<string> pumpers, float localTorque = 0f, bool localEngaged = false,
        float crank = 0f, int direction = 1, float rate = 1f) =>
        pumpers.CombinedTorque(localTorque, localEngaged, crank, direction, rate, MaxTorque, Curve, out _);

    [Test]
    public void StrokeTorque_MatchesVanilla()
    {
        // Handle ahead of the crank in its direction of travel: pushes it on.
        Assert.That(HandcarPumpers<string>.StrokeTorque(0.5f, 0f, 1, 1f, MaxTorque, Curve), Is.EqualTo(500f).Within(0.001f));
        // Behind it: pulls back, harder (acting against).
        Assert.That(HandcarPumpers<string>.StrokeTorque(-0.5f, 0f, 1, 1f, MaxTorque, Curve), Is.EqualTo(-750f).Within(0.001f));
        // Crank stopped: rate 0 counts as positive, as Unity's Mathf.Sign.
        Assert.That(HandcarPumpers<string>.StrokeTorque(0.5f, 0f, 1, 0f, MaxTorque, Curve), Is.EqualTo(500f).Within(0.001f));
    }

    [Test]
    public void TwoPumpersInTime_AddUp()
    {
        HandcarPumpers<string> pumpers = new();
        pumpers.SetBar("a", 0.75f); // handle at 0.5
        pumpers.SetEngaged("a", true);
        pumpers.SetBar("b", 0.75f);
        pumpers.SetEngaged("b", true);

        Assert.That(pumpers.EngagedCount, Is.EqualTo(2));
        Assert.That(Combined(pumpers), Is.EqualTo(1000f).Within(0.001f));
    }

    [Test]
    public void HostsOwnHandle_AddsToThePumpers()
    {
        HandcarPumpers<string> pumpers = new();
        pumpers.SetBar("a", 0.75f);
        pumpers.SetEngaged("a", true);

        Assert.That(Combined(pumpers, localTorque: 500f, localEngaged: true), Is.EqualTo(1000f).Within(0.001f));
        // Not held: vanilla's value doesn't count.
        Assert.That(Combined(pumpers, localTorque: 500f, localEngaged: false), Is.EqualTo(500f).Within(0.001f));
    }

    [Test]
    public void Total_IsCappedAtTwoPlayers()
    {
        HandcarPumpers<string> pumpers = new();
        foreach (string player in new[] { "a", "b", "c" })
        {
            pumpers.SetBar(player, 1f); // handle at 1: full push
            pumpers.SetEngaged(player, true);
        }

        Assert.That(Combined(pumpers, localTorque: MaxTorque, localEngaged: true), Is.EqualTo(2 * MaxTorque).Within(0.001f));

        // All three pulling back against the crank: capped the other way.
        foreach (string player in new[] { "a", "b", "c" })
            pumpers.SetBar(player, 0f);
        Assert.That(Combined(pumpers), Is.EqualTo(-2 * MaxTorque).Within(0.001f));
    }

    [Test]
    public void PumpersOutOfTime_CancelOut()
    {
        HandcarPumpers<string> pumpers = new();
        pumpers.SetBar("a", 0.75f); // ahead of the crank
        pumpers.SetEngaged("a", true);
        pumpers.SetBar("b", 0.25f); // behind it
        pumpers.SetEngaged("b", true);

        Assert.That(Combined(pumpers), Is.LessThan(0f)); // acting against counts 1.5 times
    }

    [Test]
    public void LettingGo_Leaving_AndGoingOutOfReach_StopTheirPush()
    {
        HandcarPumpers<string> pumpers = new();
        foreach (string player in new[] { "a", "b", "c" })
        {
            pumpers.SetBar(player, 0.75f);
            pumpers.SetEngaged(player, true);
        }

        pumpers.SetEngaged("a", false);
        Assert.That(pumpers.EngagedCount, Is.EqualTo(2));
        pumpers.Remove("b");
        Assert.That(pumpers.EngagedCount, Is.EqualTo(1));
        pumpers.RemoveWhere(player => player == "c");
        Assert.That(pumpers.EngagedCount, Is.EqualTo(0));
        Assert.That(Combined(pumpers), Is.EqualTo(0f));
    }

    [Test]
    public void BarBeforeEngaged_CountsOnceHeld()
    {
        HandcarPumpers<string> pumpers = new();
        pumpers.SetBar("a", 0.75f);
        Assert.That(pumpers.EngagedCount, Is.EqualTo(0));
        pumpers.SetEngaged("a", true);

        Assert.That(pumpers.CombinedTorque(0f, false, 0f, 1, 1f, MaxTorque, Curve, out float first), Is.EqualTo(500f).Within(0.001f));
        Assert.That(first, Is.EqualTo(0.5f).Within(0.001f));
    }
}
