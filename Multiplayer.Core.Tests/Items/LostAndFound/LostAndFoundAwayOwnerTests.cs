using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.LostAndFound;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.LostAndFound;

/// <summary>
/// An owner who isn't connected counts as far away (lost-and-found.md, "The collection"; decided by the user
/// 2026-10-21): their items are collected under the usual nearby-player and grace rules, from train
/// interiors, shelves, couplers and snap points as well as the world. An online owner's still come from
/// the world only.
/// </summary>
[TestFixture]
public sealed class LostAndFoundAwayOwnerTests
{
    private static LostItemCandidate AwayOwners(LivePlacement placement = LivePlacement.World) => new()
    {
        NetId = 42,
        Revision = 7,
        // An offline owner is an identity with no session.
        Owner = new System.Guid(9, 0, 0, new byte[8]),
        OwnerConnected = false,
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
        item.OwnerConnected = true;
        item.OwnerPositionKnown = true;
        item.OwnerDistanceSquared = 5000 * 5000;

        Assert.That(Evaluate(item).Reason, Is.EqualTo("not-in-world"));
    }

    // An end-of-train lantern on a coupler, an item on a hanger: collected detached (OI-LF-04).
    [TestCase(LivePlacement.Attached)]
    [TestCase(LivePlacement.SnappedAttachment)]
    public void AnAwayOwnersAttachedOrSnappedItem_IsCollectedToo(LivePlacement placement)
    {
        LostItemCandidate item = AwayOwners(placement);
        item.IsSnapped = true;

        Assert.That(Evaluate(item).ShouldCollect, Is.True);
    }

    [TestCase(LivePlacement.Attached)]
    [TestCase(LivePlacement.SnappedAttachment)]
    public void AnOnlineOwnersAttachedOrSnappedItem_StaysWhereItIs(LivePlacement placement)
    {
        LostItemCandidate item = AwayOwners(placement);
        item.IsSnapped = true;
        item.OwnerConnected = true;
        item.OwnerPositionKnown = true;
        item.OwnerDistanceSquared = 5000 * 5000;

        Assert.That(Evaluate(item).Kind, Is.EqualTo(LostItemDecisionKind.Ineligible));
    }

    [Test]
    public void AnOnlineOwnersSnappedItemInTheWorld_StaysWhereItIs()
    {
        LostItemCandidate item = AwayOwners();
        item.IsSnapped = true;
        item.OwnerConnected = true;
        item.OwnerPositionKnown = true;
        item.OwnerDistanceSquared = 5000 * 5000;

        Assert.That(Evaluate(item).Reason, Is.EqualTo("actively-contained-or-interacted"));
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
        item.Owner = System.Guid.Empty;

        Assert.That(Evaluate(item).Reason, Is.EqualTo("unowned"));
    }
}
