using Multiplayer.Core.Items.Inventory;
using Multiplayer.Core.Items.Projection;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Inventory;

[TestFixture]
public sealed class BeltItemPolicyTests
{
    [Test]
    public void ReleaseFrame_BeforeTheBeltTakesTheItem_IsHeld()
    {
        Assert.That(BeltItemPolicy.HoldVrRelease(beltSnappable: true, releaseFrame: 100,
            currentFrame: 100, grabbed: false, inLocalInventory: false), Is.True);
    }

    [Test]
    public void ReleaseFrame_OnceTheBeltHasTheItem_IsNotHeld()
    {
        // ForceSnapRequestUngrabBehavior snaps during the ungrab itself.
        Assert.That(BeltItemPolicy.HoldVrRelease(beltSnappable: true, releaseFrame: 100,
            currentFrame: 100, grabbed: false, inLocalInventory: true), Is.False);
    }

    [Test]
    public void NextFrame_IsNotHeld()
    {
        // The belt took it or it was really dropped: either way the observation is true now.
        Assert.That(BeltItemPolicy.HoldVrRelease(beltSnappable: true, releaseFrame: 100,
            currentFrame: 101, grabbed: false, inLocalInventory: false), Is.False);
    }

    [Test]
    public void ItemTheBeltRefuses_IsNotHeld()
    {
        Assert.That(BeltItemPolicy.HoldVrRelease(beltSnappable: false, releaseFrame: 100,
            currentFrame: 100, grabbed: false, inLocalInventory: false), Is.False);
    }

    [Test]
    public void GrabbedAgain_IsNotHeld()
    {
        Assert.That(BeltItemPolicy.HoldVrRelease(beltSnappable: true, releaseFrame: 100,
            currentFrame: 100, grabbed: true, inLocalInventory: false), Is.False);
    }

    [Test]
    public void NoRelease_IsNotHeld()
    {
        Assert.That(BeltItemPolicy.HoldVrRelease(beltSnappable: true, releaseFrame: -1,
            currentFrame: -1, grabbed: false, inLocalInventory: false), Is.False);
    }

    [Test]
    public void OwnInventoryAcknowledgement_KeepsTheBeltSnap()
    {
        Assert.That(BeltItemPolicy.KeepBeltSnap(WireItemState.InInventory, targetPlayerId: 2,
            localPlayerId: 2, snappedToBelt: true, inLocalInventory: true), Is.True);
    }

    [TestCase(WireItemState.Dropped)]
    [TestCase(WireItemState.InHand)]
    [TestCase(WireItemState.Attached)]
    public void AnyOtherPlacement_Unsnaps(WireItemState state)
    {
        Assert.That(BeltItemPolicy.KeepBeltSnap(state, targetPlayerId: 2,
            localPlayerId: 2, snappedToBelt: true, inLocalInventory: true), Is.False);
    }

    [Test]
    public void AnotherPlayersInventory_Unsnaps()
    {
        Assert.That(BeltItemPolicy.KeepBeltSnap(WireItemState.InInventory, targetPlayerId: 3,
            localPlayerId: 2, snappedToBelt: true, inLocalInventory: true), Is.False);
    }

    [Test]
    public void NoPlayer_Unsnaps()
    {
        // The InInventory/player 0 records the playtest left behind must not pin an item anywhere.
        Assert.That(BeltItemPolicy.KeepBeltSnap(WireItemState.InInventory, targetPlayerId: 0,
            localPlayerId: 0, snappedToBelt: true, inLocalInventory: true), Is.False);
    }

    [Test]
    public void OtherSnapPoint_Unsnaps()
    {
        Assert.That(BeltItemPolicy.KeepBeltSnap(WireItemState.InInventory, targetPlayerId: 2,
            localPlayerId: 2, snappedToBelt: false, inLocalInventory: true), Is.False);
    }

    [Test]
    public void SnappedButNotInTheInventory_Unsnaps()
    {
        Assert.That(BeltItemPolicy.KeepBeltSnap(WireItemState.InInventory, targetPlayerId: 2,
            localPlayerId: 2, snappedToBelt: true, inLocalInventory: false), Is.False);
    }

    [Test]
    public void OwnHoldEcho_AfterTheBeltTookTheItem_IsSuperseded()
    {
        // Playtest 2026-10-19: taken off the belt and clipped back, the echo of the InHand sent as
        // the hand let go arrived a frame after the belt had the item, and unsnapped it.
        Assert.That(BeltItemPolicy.IsSupersededOwnHold(isHost: false, localPlayerId: 2,
            originatingPlayerId: 2, WireItemState.InHand, grabbed: false, inLocalInventory: true), Is.True);
    }

    [Test]
    public void OwnHoldEcho_WhileStillHolding_IsApplied()
    {
        Assert.That(BeltItemPolicy.IsSupersededOwnHold(isHost: false, localPlayerId: 2,
            originatingPlayerId: 2, WireItemState.InHand, grabbed: true, inLocalInventory: false), Is.False);
    }

    [Test]
    public void OwnHoldEcho_AfterADrop_IsApplied()
    {
        // Out of the inventory: the drop we sent since is in flight and will settle it.
        Assert.That(BeltItemPolicy.IsSupersededOwnHold(isHost: false, localPlayerId: 2,
            originatingPlayerId: 2, WireItemState.InHand, grabbed: false, inLocalInventory: false), Is.False);
    }

    [Test]
    public void HostCorrection_IsApplied()
    {
        Assert.That(BeltItemPolicy.IsSupersededOwnHold(isHost: false, localPlayerId: 2,
            originatingPlayerId: 0, WireItemState.InHand, grabbed: false, inLocalInventory: true), Is.False);
    }

    [Test]
    public void AnotherPlayersHold_IsApplied()
    {
        Assert.That(BeltItemPolicy.IsSupersededOwnHold(isHost: false, localPlayerId: 2,
            originatingPlayerId: 3, WireItemState.InHand, grabbed: false, inLocalInventory: true), Is.False);
    }

    [Test]
    public void OwnInventoryEcho_IsApplied()
    {
        Assert.That(BeltItemPolicy.IsSupersededOwnHold(isHost: false, localPlayerId: 2,
            originatingPlayerId: 2, WireItemState.InInventory, grabbed: false, inLocalInventory: true), Is.False);
    }

    [Test]
    public void OnTheHost_IsApplied()
    {
        Assert.That(BeltItemPolicy.IsSupersededOwnHold(isHost: true, localPlayerId: 1,
            originatingPlayerId: 1, WireItemState.InHand, grabbed: false, inLocalInventory: true), Is.False);
    }
}
