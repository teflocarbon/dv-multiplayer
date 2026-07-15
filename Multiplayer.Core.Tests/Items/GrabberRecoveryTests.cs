using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class GrabberRecoveryTests
{
    [Test]
    public void HoldingReferenceThatNoLongerReportsGrabbed_IsReleased()
    {
        Assert.That(Plan(GrabberRuntimeState.Holding, true, false, false),
            Is.EqualTo(GrabberRecoveryAction.ReleaseStaleHeldItem));
    }

    [TestCase(GrabberRuntimeState.Holding)]
    [TestCase(GrabberRuntimeState.Dragging)]
    [TestCase(GrabberRuntimeState.DraggingWhileHeld)]
    public void NonIdleStateWithoutAnyInteractionReference_IsReset(GrabberRuntimeState state)
    {
        Assert.That(Plan(state, false, false, false),
            Is.EqualTo(GrabberRecoveryAction.ResetEmptyState));
    }

    [Test]
    public void HealthyHeldItem_IsNeverRecovered()
    {
        Assert.That(Plan(GrabberRuntimeState.Holding, true, true, false),
            Is.EqualTo(GrabberRecoveryAction.None));
    }

    [Test]
    public void HealthyDrag_IsNeverRecovered()
    {
        Assert.That(Plan(GrabberRuntimeState.Dragging, false, false, true),
            Is.EqualTo(GrabberRecoveryAction.None));
    }

    [Test]
    public void IdleStateWithStaleLookingReference_IsLeftToBaseGame()
    {
        Assert.That(Plan(GrabberRuntimeState.Idle, true, false, false),
            Is.EqualTo(GrabberRecoveryAction.None));
    }

    private static GrabberRecoveryAction Plan(GrabberRuntimeState state,
        bool held, bool grabbed, bool dragged) => GrabberRecoveryPlanner.Plan(
        new GrabberRecoveryInput(state, held, grabbed, dragged));
}
