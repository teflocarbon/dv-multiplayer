using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class ItemSnapshotApplicationPolicyTests
{
    [Test]
    public void OriginatingClientAcknowledgesThrowWithoutReapplyingPhysics()
    {
        Assert.That(ItemSnapshotApplicationPolicy.IsLocalThrowAcknowledgement(
            false, 2, 2, WireItemState.Thrown), Is.True);
    }

    [TestCase(1, 2, WireItemState.Thrown)]
    [TestCase(2, 2, WireItemState.Dropped)]
    [TestCase(0, 0, WireItemState.Thrown)]
    public void OtherPlayersAndOtherStatesStillApply(int localPlayerId,
        int originatingPlayerId, WireItemState state)
    {
        Assert.That(ItemSnapshotApplicationPolicy.IsLocalThrowAcknowledgement(
            false, (byte)localPlayerId, (byte)originatingPlayerId, state), Is.False);
    }

    [Test]
    public void HostNeverUsesClientAcknowledgementShortcut()
    {
        Assert.That(ItemSnapshotApplicationPolicy.IsLocalThrowAcknowledgement(
            true, 2, 2, WireItemState.Thrown), Is.False);
    }

    [TestCase(1)]
    [TestCase(2)]
    public void OriginatingVrClientAcknowledgesDropWithoutZeroingNativeReleaseVelocity(int hand)
    {
        Assert.That(ItemSnapshotApplicationPolicy.IsLocalVrReleaseAcknowledgement(
            false, 2, 2, WireItemState.InHand, WireItemState.Dropped, (byte)hand), Is.True);
    }

    [TestCase(true, 2, 2, WireItemState.InHand, WireItemState.Dropped, 1)]
    [TestCase(false, 1, 2, WireItemState.InHand, WireItemState.Dropped, 1)]
    [TestCase(false, 2, 2, WireItemState.Dropped, WireItemState.Dropped, 1)]
    [TestCase(false, 2, 2, WireItemState.InHand, WireItemState.Thrown, 1)]
    [TestCase(false, 2, 2, WireItemState.InHand, WireItemState.Dropped, 0)]
    public void VrReleaseShortcutRequiresOriginatingClientHeldVrDrop(bool isHost,
        int localPlayer, int origin, WireItemState previous, WireItemState received, int hand)
    {
        Assert.That(ItemSnapshotApplicationPolicy.IsLocalVrReleaseAcknowledgement(
            isHost, (byte)localPlayer, (byte)origin, previous, received, (byte)hand), Is.False);
    }
}
