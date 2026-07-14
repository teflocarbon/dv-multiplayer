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
}
