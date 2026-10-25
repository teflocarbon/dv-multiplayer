using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.WorldItems;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.WorldItems;

[TestFixture]
public sealed class OfficeStockRulesTests
{
    [Test]
    public void APlayerNearProtectsIt() =>
        Assert.That(OfficeStockRules.Protects(true, true), Is.True);

    [Test]
    public void APlayerFarDoesnt() =>
        Assert.That(OfficeStockRules.Protects(true, false), Is.False);

    [Test]
    public void APlayerStillLoadingProtectsEverywhere() =>
        // F30: their place isn't known yet.
        Assert.That(OfficeStockRules.Protects(false, false), Is.True);

    [TestCase(LivePlacement.World)]
    [TestCase(LivePlacement.StaticParent)]
    [TestCase(LivePlacement.TrainInterior)]
    public void ALooseUnclaimedForkIsCleared(LivePlacement placement) =>
        Assert.That(OfficeStockRules.MayClearFork(false, false, false, placement, false, false), Is.True);

    [Test]
    public void NeverOneWithABetterClaim()
    {
        Assert.That(OfficeStockRules.MayClearFork(true, false, false, LivePlacement.World, false, false), Is.False, "owned");
        Assert.That(OfficeStockRules.MayClearFork(false, true, false, LivePlacement.World, false, false), Is.False, "claimed");
        Assert.That(OfficeStockRules.MayClearFork(false, false, true, LivePlacement.World, false, false), Is.False, "in Lost and Found");
        Assert.That(OfficeStockRules.MayClearFork(false, false, false, LivePlacement.World, true, false), Is.False, "simulated");
        Assert.That(OfficeStockRules.MayClearFork(false, false, false, LivePlacement.World, false, true), Is.False, "grabbed");
    }

    [TestCase(LivePlacement.PlayerHand)]
    [TestCase(LivePlacement.PlayerInventory)]
    [TestCase(LivePlacement.Container)]
    [TestCase(LivePlacement.Installed)]
    [TestCase(LivePlacement.Magazine)]
    public void NorOneHeldOrStored(LivePlacement placement) =>
        Assert.That(OfficeStockRules.MayClearFork(false, false, false, placement, false, false), Is.False);

    [Test]
    public void AKnockedPropIsPutBackWhenNobodySeesEither() =>
        Assert.That(OfficeStockRules.MayReset(false, false, false, false), Is.True);

    [Test]
    public void NotWhileSomeoneIsBesideIt() =>
        // OI-SYNC-13: knocked out of the office, nobody at its place, the player beside it.
        Assert.That(OfficeStockRules.MayReset(false, true, false, false), Is.False);

    [Test]
    public void NorWhileItsPlaceIsSeenOrItMoves()
    {
        Assert.That(OfficeStockRules.MayReset(true, false, false, false), Is.False);
        Assert.That(OfficeStockRules.MayReset(false, false, true, false), Is.False);
        Assert.That(OfficeStockRules.MayReset(false, false, false, true), Is.False);
    }
}
