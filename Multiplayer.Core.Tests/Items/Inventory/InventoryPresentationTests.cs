using Multiplayer.Core.Items.Inventory;
using Multiplayer.Core.Items.Ledger;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Inventory;

[TestFixture]
public sealed class InventoryPresentationTests
{
    [Test]
    public void ForeignRecallableItem_ThrownFromLocalInventory_PurgesThiefSilhouette()
    {
        InventoryPresentationInput input = new(735, ItemRelation.SomeoneElses, true,
            retrievalEligible: true, hasRetrievalClaim: false, isLostAndFound: false);

        Assert.That(InventoryPresentationPlanner.ShouldPurgeForeignDroppedClaim(input,
            localSlotDropped: true, localSlotReservedOrLocked: true,
            localPossessionActive: false), Is.True);
    }

    [Test]
    public void ForeignRecallableItem_StillHeld_DoesNotPurgeInventoryMembership()
    {
        InventoryPresentationInput input = new(735, ItemRelation.SomeoneElses, true,
            retrievalEligible: true, hasRetrievalClaim: false, isLostAndFound: false);

        Assert.That(InventoryPresentationPlanner.ShouldPurgeForeignDroppedClaim(input,
            localSlotDropped: false, localSlotReservedOrLocked: true,
            localPossessionActive: true), Is.False);
    }

    [Test]
    public void ForeignRecallableItem_EquippedWithReservedSlot_DoesNotPurgePossession()
    {
        InventoryPresentationInput input = new(735, ItemRelation.SomeoneElses, true,
            retrievalEligible: true, hasRetrievalClaim: false, isLostAndFound: false);

        Assert.That(InventoryPresentationPlanner.ShouldPurgeForeignDroppedClaim(input,
            localSlotDropped: true, localSlotReservedOrLocked: true,
            localPossessionActive: true), Is.False);
    }

    [Test]
    public void UnmanagedItem_UsesVanillaPresentationAndRecall()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(0, ItemRelation.Nobodys, true, false, false, false));

        Assert.That(result.NetworkManaged, Is.False);
        Assert.That(result.ShowForeignStyle, Is.False);
        Assert.That(result.AllowRecall, Is.True);
        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.Vanilla));
    }

    [Test]
    public void UnownedItem_IsNeverRed()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, ItemRelation.Nobodys, true, true, true, false));

        Assert.That(result.ShowForeignStyle, Is.False);
        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.Vanilla));
    }

    [Test]
    public void ForeignEssential_IsRedAndCannotBeRecalled()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, ItemRelation.SomeoneElses, true, true, false, false));

        Assert.That(result.ForeignOwned, Is.True);
        Assert.That(result.ShowForeignStyle, Is.True);
        Assert.That(result.AllowRecall, Is.False);
        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.BlockForeignOwner));
    }

    // D3: red for every item someone else owns, not just a recallable essential.
    [Test]
    public void ForeignOrdinaryItem_IsRed()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, ItemRelation.SomeoneElses, true, false, false, false));

        Assert.That(result.ForeignOwned, Is.True);
        Assert.That(result.ShowForeignStyle, Is.True);
        Assert.That(result.AllowRecall, Is.False);
        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.BlockForeignOwner));
    }

    [Test]
    public void ForeignItemInLostAndFound_IsRed()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, ItemRelation.SomeoneElses, true, true, false, true));

        Assert.That(result.ShowForeignStyle, Is.True);
        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.BlockForeignOwner));
    }

    [Test]
    public void OwnItem_IsNotRed()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, ItemRelation.Mine, true, false, false, false));

        Assert.That(result.ShowForeignStyle, Is.False);
        Assert.That(result.ForeignOwned, Is.False);
    }

    [Test]
    public void OwnedWorldItem_UsesNetworkRecall()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, ItemRelation.Mine, true, true, true, false));

        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.NetworkRecall));
    }

    [Test]
    public void OwnedLostItem_UsesLostAndFoundRetrieval()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, ItemRelation.Mine, true, true, true, true));

        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.LostAndFoundRetrieval));
    }

    [Test]
    public void NoLocalPlayer_FallsBackWithoutMarkingForeign()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, ItemRelation.SomeoneElses, false, true, true, false));

        Assert.That(result.ShowForeignStyle, Is.False);
        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.Vanilla));
    }

    [Test]
    public void OwnedReservedButIneligibleItem_CannotUseNetworkRecall()
    {
        InventoryItemPresentation result = InventoryPresentationPlanner.Plan(
            new InventoryPresentationInput(735, ItemRelation.Mine, true, false, true, false));

        Assert.That(result.AllowRecall, Is.False);
        Assert.That(result.RecallRoute, Is.EqualTo(InventoryRecallRoute.BlockNotRecallable));
    }

    [Test]
    public void Relation_FromOwnerAndViewer()
    {
        System.Guid owner = System.Guid.NewGuid(), other = System.Guid.NewGuid();
        Assert.That(ItemRelations.Of(System.Guid.Empty, owner), Is.EqualTo(ItemRelation.Nobodys));
        Assert.That(ItemRelations.Of(owner, owner), Is.EqualTo(ItemRelation.Mine));
        Assert.That(ItemRelations.Of(owner, other), Is.EqualTo(ItemRelation.SomeoneElses));
        // A dedicated host's own world has no viewer: every owned item is someone else's.
        Assert.That(ItemRelations.Of(owner, System.Guid.Empty), Is.EqualTo(ItemRelation.SomeoneElses));
    }
}
