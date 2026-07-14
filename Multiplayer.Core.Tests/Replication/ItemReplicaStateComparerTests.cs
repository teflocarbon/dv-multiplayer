using Multiplayer.Core.Replication;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Replication;

[TestFixture]
public sealed class ItemReplicaStateComparerTests
{
    [Test]
    public void IgnoresProcessLocalFieldsByConstruction()
    {
        ItemReplicaState expected = Base("Dropped");
        ItemReplicaState actual = Base("Dropped");

        Assert.That(ItemReplicaStateComparer.Compare(expected, actual), Is.Empty);
    }

    [Test]
    public void DetectsRendererCorruptionWhenHierarchyMatches()
    {
        ItemReplicaState expected = Base("Dropped");
        expected.RendererCount = 67; expected.RendererEnabledCount = 64;
        ItemReplicaState actual = Base("Dropped");
        actual.RendererCount = 67; actual.RendererEnabledCount = 67;

        Assert.That(ItemReplicaStateComparer.Compare(expected, actual),
            Has.Some.Matches<ItemReplicaDifference>(value => value.Code == "renderer-state-mismatch"));
    }

    [Test]
    public void DoesNotCompareRendererMaskWhileHierarchyIsStillConstructing()
    {
        ItemReplicaState expected = Base("InHand");
        expected.RendererCount = 67; expected.RendererEnabledCount = 64;
        ItemReplicaState actual = Base("InHand");
        actual.RendererCount = 4; actual.RendererEnabledCount = 3;

        Assert.That(ItemReplicaStateComparer.Compare(expected, actual),
            Has.None.Matches<ItemReplicaDifference>(value => value.Code == "renderer-state-mismatch"));
    }

    [Test]
    public void AcceptsThrownAsDroppedAndDoesNotTreatImmediateVelocityAsTrajectoryFailure()
    {
        ItemReplicaState expected = Base("Thrown");
        ItemReplicaState actual = Base("Dropped");
        actual.Velocity = new ReplicaVector3(0, 0, 0);

        var result = ItemReplicaStateComparer.Compare(expected, actual);
        Assert.That(result, Has.None.Matches<ItemReplicaDifference>(value => value.Code == "item-state-mismatch"));
        Assert.That(result, Has.None.Matches<ItemReplicaDifference>(value => value.Code == "thrown-item-lost-momentum"));
    }

    [Test]
    public void DetectsAuthorityHolderAndAbsolutePositionDiscontinuities()
    {
        ItemReplicaState expected = Base("InHand");
        expected.PlacementPlayerId = 2;
        ItemReplicaState actual = Base("InHand");
        actual.AuthorityRevision = 8;
        actual.PlacementPlayerId = 1;
        actual.ActualRemoteHolderPlayerId = 1;

        var result = ItemReplicaStateComparer.Compare(expected, actual);
        Assert.That(result, Has.Some.Matches<ItemReplicaDifference>(value => value.Code == "authority-revision-mismatch"));
        Assert.That(result, Has.Some.Matches<ItemReplicaDifference>(value => value.Code == "placement-player-mismatch"));
        Assert.That(result, Has.Some.Matches<ItemReplicaDifference>(value => value.Code == "remote-hand-holder-mismatch"));
    }

    [Test]
    public void LocalHolderDoesNotRequireRemoteHandRepresentation()
    {
        ItemReplicaState expected = Base("InHand");
        expected.PlacementPlayerId = 2;
        ItemReplicaState actual = Base("InHand");
        actual.PlacementPlayerId = 2;
        actual.ActualRemoteHolderPlayerId = 0;

        Assert.That(ItemReplicaStateComparer.Compare(expected, actual, observerPlayerId: 2),
            Has.None.Matches<ItemReplicaDifference>(value => value.Code == "remote-hand-holder-mismatch"));
        Assert.That(ItemReplicaStateComparer.Compare(expected, actual, observerPlayerId: 1),
            Has.Some.Matches<ItemReplicaDifference>(value => value.Code == "remote-hand-holder-mismatch"));
    }

    [Test]
    public void DetectsPageBookSemanticMismatchAfterBothSidesGenerate()
    {
        ItemReplicaState expected = Base("Dropped");
        expected.PageBookPagesGenerated = true;
        expected.PageBookCurrentPage = 4;
        expected.PageBookPageCount = 61;
        ItemReplicaState actual = Base("Dropped");
        actual.PageBookPagesGenerated = true;
        actual.PageBookCurrentPage = 3;
        actual.PageBookPageCount = 60;

        var result = ItemReplicaStateComparer.Compare(expected, actual);
        Assert.That(result, Has.Some.Matches<ItemReplicaDifference>(value => value.Code == "pagebook-current-page-mismatch"));
        Assert.That(result, Has.Some.Matches<ItemReplicaDifference>(value => value.Code == "pagebook-page-count-mismatch"));
    }

    [Test]
    public void DefersPageBookComparisonWhileReplicaIsStillGenerating()
    {
        ItemReplicaState expected = Base("Dropped");
        expected.PageBookPagesGenerated = true;
        expected.PageBookCurrentPage = 4;
        ItemReplicaState actual = Base("Dropped");
        actual.PageBookPagesGenerated = false;
        actual.PageBookCurrentPage = 0;

        Assert.That(ItemReplicaStateComparer.Compare(expected, actual),
            Has.None.Matches<ItemReplicaDifference>(value => value.Code.StartsWith("pagebook-")));
    }

    private static ItemReplicaState Base(string state) => new()
    {
        ItemState = state,
        AuthorityRevision = 7,
        PersistentOwnerPlayerId = 1,
        InventoryClaimPlayerId = 1,
        InventoryClaimSlot = 2,
        InventoryClaimFlags = "Reserved, Dropped",
        PlacementPlayerId = state is "InHand" or "InInventory" ? (byte)2 : (byte)0,
        ActualRemoteHolderPlayerId = state == "InHand" ? (byte)2 : (byte)0,
        PositionAbsolute = new ReplicaVector3(100, 20, 300),
        Velocity = new ReplicaVector3(1, 1, 1),
        ActiveInHierarchy = true,
        Parent = state is "Dropped" or "Thrown" ? "origin_shift_parent" : "Player[P2]",
        LayerName = "World_Item",
        RigidbodyIsKinematic = false
    };
}
