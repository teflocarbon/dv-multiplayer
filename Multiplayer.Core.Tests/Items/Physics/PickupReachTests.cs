using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.Physics;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Physics;

[TestFixture]
public sealed class PickupReachTests
{
    [TestCase(LivePlacement.World)]
    [TestCase(LivePlacement.StaticParent)]
    [TestCase(LivePlacement.TrainInterior)]
    [TestCase(LivePlacement.Attached)]
    [TestCase(LivePlacement.SnappedAttachment)]
    public void AnItemLyingNearIsInReach(LivePlacement placement) =>
        Assert.That(PickupReach.Within(placement, 2f * 2f), Is.True);

    [TestCase(LivePlacement.World)]
    [TestCase(LivePlacement.StaticParent)]
    [TestCase(LivePlacement.TrainInterior)]
    [TestCase(LivePlacement.Attached)]
    [TestCase(LivePlacement.SnappedAttachment)]
    public void OneAcrossTheMapIsnt(LivePlacement placement) =>
        // F35: before R7 the server took it; a lantern snapped on a car, before the R7 fixes (B3).
        Assert.That(PickupReach.Within(placement, 40f * 40f), Is.False);

    [Test]
    public void TheBoundItself() =>
        Assert.That(PickupReach.Within(LivePlacement.World, PickupReach.Metres * PickupReach.Metres), Is.True);

    [TestCase(LivePlacement.PlayerHand)]
    [TestCase(LivePlacement.PlayerInventory)]
    [TestCase(LivePlacement.Container)]
    [TestCase(LivePlacement.Magazine)]
    public void ANonWorldPlaceHasItsOwnRules(LivePlacement placement) =>
        // Not a pickup from where it lies: their own rules decide.
        Assert.That(PickupReach.Within(placement, 500f * 500f), Is.True);
}
