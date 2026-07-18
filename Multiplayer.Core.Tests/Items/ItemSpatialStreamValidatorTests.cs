using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class ItemSpatialStreamValidatorTests
{
    private static readonly ItemSpatialLeaseToken Lease = new(2, 17, 4, 22, true);

    [Test]
    public void NewSampleFromLeaseHolderIsAccepted() =>
        Assert.That(ItemSpatialStreamValidator.Validate(Lease, new(2, 2, 17, 4, 23)), Is.Empty);

    [TestCase(3, 2, "sender-not-simulator")]
    [TestCase(2, 3, "sender-not-simulator")]
    public void SimulatorCapabilityCannotBeBorrowed(byte sender, byte claimedSimulator, string reason) =>
        Assert.That(ItemSpatialStreamValidator.Validate(Lease,
            new(sender, claimedSimulator, 17, 4, 23)), Is.EqualTo(reason));

    [TestCase(16u, 4u, 23u, "stale-spatial-authority-revision")]
    [TestCase(17u, 3u, 23u, "stale-simulation-epoch")]
    [TestCase(17u, 4u, 22u, "stale-sample-sequence")]
    [TestCase(17u, 4u, 21u, "stale-sample-sequence")]
    public void OldStreamsCannotMutateCurrentAuthority(uint revision, uint epoch, uint sequence,
        string reason) =>
        Assert.That(ItemSpatialStreamValidator.Validate(Lease,
            new(2, 2, revision, epoch, sequence)), Is.EqualTo(reason));

    [Test]
    public void LogicalPlacementSupersedesAnOtherwiseCurrentSample()
    {
        ItemSpatialLeaseToken movedToInventory = new(Lease.SimulatorPlayerId, Lease.AuthorityRevision,
            Lease.SimulationEpoch, Lease.LastSequence, false);
        Assert.That(ItemSpatialStreamValidator.Validate(movedToInventory,
            new(2, 2, 17, 4, 23)), Is.EqualTo("spatial-placement-not-simulatable"));
    }
}
