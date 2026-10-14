using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class RemoteItemProjectionPlannerTests
{
    [TestCase(WireItemState.InHand, RemoteItemProjectionAction.LocalHand, true, false)]
    [TestCase(WireItemState.InInventory, RemoteItemProjectionAction.LocalInventory, false, false)]
    public void LocalPlayerProjection_ClearsRemoteRepresentations(WireItemState state,
        RemoteItemProjectionAction action, bool activate, bool deactivate)
    {
        RemoteItemProjectionPlan plan = RemoteItemProjectionPlanner.Plan(state, 2, 2, false);
        Assert.That(plan.Action, Is.EqualTo(action));
        Assert.That(plan.ClearExistingRemoteHands, Is.True);
        Assert.That(plan.Activate, Is.EqualTo(activate));
        Assert.That(plan.Deactivate, Is.EqualTo(deactivate));
    }

    [TestCase(WireItemState.InHand, RemoteItemProjectionAction.RemoteHand, true, false)]
    [TestCase(WireItemState.InInventory, RemoteItemProjectionAction.RemoteInventory, false, true)]
    public void ExistingRemotePlayer_GetsCorrectProjection(WireItemState state,
        RemoteItemProjectionAction action, bool activate, bool deactivate)
    {
        RemoteItemProjectionPlan plan = RemoteItemProjectionPlanner.Plan(state, 1, 2, true);
        Assert.That(plan.Action, Is.EqualTo(action));
        Assert.That(plan.ClearExistingRemoteHands, Is.False);
        Assert.That(plan.Activate, Is.EqualTo(activate));
        Assert.That(plan.Deactivate, Is.EqualTo(deactivate));
    }

    [TestCase(WireItemState.InHand)]
    [TestCase(WireItemState.InInventory)]
    public void MissingRemotePlayer_DeactivatesInsteadOfInventingHolder(WireItemState state)
    {
        RemoteItemProjectionPlan plan = RemoteItemProjectionPlanner.Plan(state, 1, 2, false);
        Assert.That(plan.Action, Is.EqualTo(RemoteItemProjectionAction.MissingRemotePlayer));
        Assert.That(plan.Deactivate, Is.True);
    }

    [TestCase(WireItemState.Dropped)]
    [TestCase(WireItemState.Thrown)]
    [TestCase(WireItemState.Attached)]
    [TestCase(WireItemState.Removed)]
    public void NonPlayerState_IsRejectedByHandProjectionPlanner(WireItemState state)
    {
        Assert.That(RemoteItemProjectionPlanner.Plan(state, 1, 2, true).Action,
            Is.EqualTo(RemoteItemProjectionAction.InvalidState));
    }

    [Test]
    public void PlayerStateWithZeroPlayerId_IsInvalid()
    {
        Assert.That(RemoteItemProjectionPlanner.Plan(WireItemState.InHand, 0, 2, true).Action,
            Is.EqualTo(RemoteItemProjectionAction.InvalidState));
    }

    [Test]
    public void HostWithoutLocalPlayerModel_UsesExistingRemoteProjection()
    {
        RemoteItemProjectionPlan plan = RemoteItemProjectionPlanner.Plan(
            WireItemState.InHand, 1, 0, true);
        Assert.That(plan.Action, Is.EqualTo(RemoteItemProjectionAction.RemoteHand));
    }
}
