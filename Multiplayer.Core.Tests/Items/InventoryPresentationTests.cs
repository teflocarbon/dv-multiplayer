using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class InventoryPresentationTests
{
    [Test]
    public void ForeignRecallableItem_ThrownFromLocalInventory_PurgesThiefSilhouette()
    {
        InventoryPresentationInput input = new(735, 1, 2,
            retrievalEligible: true, hasRetrievalClaim: true, isLostAndFound: false);

        Assert.That(InventoryPresentationPlanner.ShouldPurgeForeignDroppedClaim(input,
            localSlotDropped: true, localSlotReservedOrLocked: true,
            localPossessionActive: false), Is.True);
    }

    [Test]
    public void ForeignRecallableItem_StillHeld_DoesNotPurgeInventoryMembership()
    {
        InventoryPresentationInput input = new(735, 1, 2,
            retrievalEligible: true, hasRetrievalClaim: true, isLostAndFound: false);

        Assert.That(InventoryPresentationPlanner.ShouldPurgeForeignDroppedClaim(input,
            localSlotDropped: false, localSlotReservedOrLocked: true,
            localPossessionActive: true), Is.False);
    }

    [Test]
    public void ForeignRecallableItem_EquippedWithReservedSlot_DoesNotPurgePossession()
    {
        InventoryPresentationInput input = new(735, 1, 2,
            retrievalEligible: true, hasRetrievalClaim: true, isLostAndFound: false);

        Assert.That(InventoryPresentationPlanner.ShouldPurgeForeignDroppedClaim(input,
            localSlotDropped: true, localSlotReservedOrLocked: true,
            localPossessionActive: true), Is.False);
    }

    [Test]
    public void UnmanagedItem_UsesVanillaPresentationAndRecall()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(0, 0, 1, false, false, false));

        Assert.That(result.NetworkManaged, Is.False);
        Assert.That(result.ShowForeignStyle, Is.False);
        Assert.That(result.AllowRecall, Is.True);
        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.Vanilla));
    }

    [Test]
    public void ForeignRetrievableItem_IsRedAndCannotBeRecalled()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, 2, 1, true, true, false));

        Assert.That(result.ForeignOwned, Is.True);
        Assert.That(result.ShowForeignStyle, Is.True);
        Assert.That(result.AllowRecall, Is.False);
        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.BlockForeignOwner));
    }

    [Test]
    public void ForeignItemWithoutRetrievalClaim_IsNotMarkedAsStolen()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, 2, 1, true, false, false));

        Assert.That(result.ForeignOwned, Is.False);
        Assert.That(result.ShowForeignStyle, Is.False);
        Assert.That(result.AllowRecall, Is.False);
        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.BlockForeignOwner));
    }

    [Test]
    public void OwnedWorldItem_UsesNetworkRecall()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, 1, 1, true, true, false));

        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.NetworkRecall));
    }

    [Test]
    public void OwnedLostItem_UsesLostAndFoundRetrieval()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, 1, 1, true, true, true));

        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.LostAndFoundRetrieval));
    }

    [Test]
    public void UnknownLocalPlayer_FallsBackWithoutMarkingForeign()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, 2, 0, true, true, false));

        Assert.That(result.ShowForeignStyle, Is.False);
        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.Vanilla));
    }

    [Test]
    public void ForeignReservedButIneligibleItem_IsNotRed()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, 2, 1, false, true, false));

        Assert.That(result.ForeignOwned, Is.False);
        Assert.That(result.ShowForeignStyle, Is.False);
        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.BlockForeignOwner));
    }

    [Test]
    public void OwnedReservedButIneligibleItem_CannotUseNetworkRecall()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, 1, 1, false, true, false));

        Assert.That(result.AllowRecall, Is.False);
        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.BlockNotRecallable));
    }
}
