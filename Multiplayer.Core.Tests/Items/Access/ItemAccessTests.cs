using Multiplayer.Core.Items.Access;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Access;

[TestFixture]
public sealed class ItemAccessTests
{
    [TestCase(ItemAccessAction.Stash)]
    [TestCase(ItemAccessAction.TakeFromWorld)]
    [TestCase(ItemAccessAction.Withdraw)]
    [TestCase(ItemAccessAction.RemoveGadget)]
    public void TodaysRules_LetAnyoneDoTheEverydayThings(ItemAccessAction action)
    {
        Assert.That(ItemAccess.Decide(new ItemAccessRequest(action, actorOwnsItem: false, isFixture: false)).Allowed, Is.True);
    }

    [Test]
    public void Depositing_NeedsTheItemToBeTheActorsOwn()
    {
        ItemAccessDecision foreign = ItemAccess.Decide(new ItemAccessRequest(ItemAccessAction.Deposit, false, false));

        Assert.That(foreign.Allowed, Is.False);
        Assert.That(foreign.Reason, Is.EqualTo("deposit-item-not-owned"), "the reason the deposit refused with before");
        Assert.That(ItemAccess.Decide(new ItemAccessRequest(ItemAccessAction.Deposit, true, false)).Allowed, Is.True);
    }

    [TestCase(ItemAccessAction.Retrieve)]
    [TestCase(ItemAccessAction.Recall)]
    public void RetrievingAndRecalling_AreTheOwners(ItemAccessAction action)
    {
        Assert.That(ItemAccess.Decide(new ItemAccessRequest(action, false, false)).Reason, Is.EqualTo("requester-not-persistent-owner"));
        Assert.That(ItemAccess.Decide(new ItemAccessRequest(action, true, false)).Allowed, Is.True);
    }

    [Test]
    public void TakingFromAnotherPlayer_IsRefused()
    {
        Assert.That(ItemAccess.Decide(new ItemAccessRequest(ItemAccessAction.TakeFromPlayer, false, false)).Allowed, Is.False);
    }

    [TestCase(ItemAccessAction.Stash)]
    [TestCase(ItemAccessAction.Deposit)]
    public void AFixture_CantBeStashedOrDeposited_EvenByAnOwner(ItemAccessAction action)
    {
        // The shop scanner: a player logged out holding it and kept it, and the shop had none.
        ItemAccessDecision decision = ItemAccess.Decide(new ItemAccessRequest(action, actorOwnsItem: true, isFixture: true));

        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.Reason, Is.EqualTo(ItemAccess.FixtureStaysInPlace));
    }

    [Test]
    public void AnEssential_CantBeDeposited_EvenByItsOwner()
    {
        // item-review L7: a deposited essential came back without its claim (decided by the user 2026-10-22).
        ItemAccessDecision decision = ItemAccess.Decide(new ItemAccessRequest(ItemAccessAction.Deposit,
            actorOwnsItem: true, isFixture: false, isEssential: true));

        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.Reason, Is.EqualTo(ItemAccess.EssentialStaysOut));
    }

    [Test]
    public void AnEssential_IsStashedAsUsual()
    {
        Assert.That(ItemAccess.Decide(new ItemAccessRequest(ItemAccessAction.Stash, true, false, isEssential: true)).Allowed, Is.True);
    }

    [Test]
    public void AFixture_CanStillBeCarriedInTheHand()
    {
        Assert.That(ItemAccess.Decide(new ItemAccessRequest(ItemAccessAction.TakeFromWorld, false, true)).Allowed, Is.True);
    }

    [Test]
    public void TheFixtureList_MatchesByPrefabOrAuthoredKey()
    {
        ItemFixtureList fixtures = new();
        fixtures.AddPrefab("ShopScanner");
        fixtures.AddAuthoredKey("station:HB/shop/scanner");

        Assert.That(fixtures.Contains("ShopScanner", null), Is.True);
        Assert.That(fixtures.Contains("Mug", "station:HB/shop/scanner"), Is.True);
        Assert.That(fixtures.Contains("Mug", "station:HB/office/mug-1"), Is.False);
        Assert.That(new ItemFixtureList().Contains("ShopScanner", null), Is.False, "empty until the shops work fills it");
        Assert.That(fixtures.RemovePrefab("ShopScanner") && !fixtures.Contains("ShopScanner", null), Is.True);
    }
}
