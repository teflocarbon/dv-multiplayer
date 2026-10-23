using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.LostAndFound;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.LostAndFound;

/// <summary>
/// An owner who isn't connected counts as far away (owned-items.md, PR 3; decided by the user
/// 2026-10-21): their items are collected under the usual nearby-player and grace rules, from train
/// interiors and shelves as well as the world. An online owner's still come from the world only.
/// </summary>
[TestFixture]
public sealed class LostAndFoundAwayOwnerTests
{
    private static LostItemCandidate AwayOwners(LivePlacement placement = LivePlacement.World) => new()
    {
        NetId = 42,
        Revision = 7,
        PersistentOwnerPlayerId = ItemTransition.AbsentOwnerId,
        IsPersonalItem = true,
        Placement = placement,
        // An offline owner has no position.
        OwnerPositionKnown = false,
        NearestPlayerPositionKnown = true,
        NearestPlayerDistanceSquared = 101 * 101,
        EligibleDurationSeconds = 11
    };

    private static LostItemDecision Evaluate(LostItemCandidate item) => LostItemCollectionPolicy.Evaluate(item, 200, 100, 10);

    [Test]
    public void AnAwayOwnersItemNobodyIsNear_IsCollected()
    {
        LostItemDecision decision = Evaluate(AwayOwners());

        Assert.That(decision.ShouldCollect, Is.True);
        Assert.That(decision.Reason, Is.EqualTo("owner-away"));
    }

    [TestCase(LivePlacement.TrainInterior)]
    [TestCase(LivePlacement.StaticParent)]
    public void AnAwayOwnersItemInATrainOrOnAShelf_IsCollectedToo(LivePlacement placement)
    {
        Assert.That(Evaluate(AwayOwners(placement)).ShouldCollect, Is.True);
    }

    [TestCase(LivePlacement.TrainInterior)]
    [TestCase(LivePlacement.StaticParent)]
    public void AnOnlineOwnersItemInATrainOrOnAShelf_StaysWhereItIs(LivePlacement placement)
    {
        LostItemCandidate item = AwayOwners(placement);
        item.PersistentOwnerPlayerId = 1;
        item.OwnerPositionKnown = true;
        item.OwnerDistanceSquared = 5000 * 5000;

        Assert.That(Evaluate(item).Reason, Is.EqualTo("not-in-world"));
    }

    [TestCase(LivePlacement.PlayerHand)]
    [TestCase(LivePlacement.PlayerInventory)]
    [TestCase(LivePlacement.Container)]
    [TestCase(LivePlacement.Installed)]
    public void WithAPlayer_InAContainer_OrInstalled_IsNeverCollected(LivePlacement placement)
    {
        Assert.That(Evaluate(AwayOwners(placement)).Kind, Is.EqualTo(LostItemDecisionKind.Ineligible));
    }

    [Test]
    public void APlayerNearby_StillProtectsIt()
    {
        LostItemCandidate item = AwayOwners();
        item.NearestPlayerDistanceSquared = 20 * 20;

        Assert.That(Evaluate(item).Reason, Is.EqualTo("another-player-nearby"));
    }

    [Test]
    public void WithNobodyConnected_NothingIsCollected()
    {
        LostItemCandidate item = AwayOwners();
        item.NearestPlayerPositionKnown = false;

        Assert.That(Evaluate(item).Kind, Is.EqualTo(LostItemDecisionKind.Protected));
    }

    [Test]
    public void TheGracePeriod_StillApplies()
    {
        LostItemCandidate item = AwayOwners();
        item.EligibleDurationSeconds = 3;

        Assert.That(Evaluate(item).Kind, Is.EqualTo(LostItemDecisionKind.WaitingForGrace));
    }

    [Test]
    public void AnUnownedItem_IsNeverCollected()
    {
        LostItemCandidate item = AwayOwners();
        item.PersistentOwnerPlayerId = 0;

        Assert.That(Evaluate(item).Reason, Is.EqualTo("unowned"));
    }
}
