using System;
using System.Collections.Generic;
using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class LostAndFoundTests
{
    private static LostItemCandidate Eligible() => new()
    {
        NetId = 42,
        Revision = 7,
        PersistentOwnerPlayerId = 1,
        IsPersonalItem = true,
        Placement = AuthorityPlacement.World,
        OwnerPositionKnown = true,
        OwnerDistanceSquared = 201 * 201,
        NearestPlayerPositionKnown = true,
        NearestPlayerDistanceSquared = 101 * 101,
        EligibleDurationSeconds = 11
    };

    [Test]
    public void FullyEligibleWorldItem_IsCollected()
    {
        Assert.That(LostItemCollectionPolicy.Evaluate(Eligible(), 200, 100, 10).ShouldCollect, Is.True);
    }

    [TestCase(AuthorityPlacement.PlayerHand)]
    [TestCase(AuthorityPlacement.PlayerInventory)]
    [TestCase(AuthorityPlacement.Container)]
    [TestCase(AuthorityPlacement.Attached)]
    [TestCase(AuthorityPlacement.Installed)]
    [TestCase(AuthorityPlacement.LostAndFound)]
    public void NonWorldPlacement_IsNeverCollected(AuthorityPlacement placement)
    {
        LostItemCandidate item = Eligible();
        item.Placement = placement;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).Kind,
            Is.EqualTo(LostItemDecisionKind.Ineligible));
    }

    [Test]
    public void NearbyOtherPlayer_ProtectsItemEvenWhenOwnerIsFar()
    {
        LostItemCandidate item = Eligible();
        item.NearestPlayerDistanceSquared = 25;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).Reason,
            Is.EqualTo("another-player-nearby"));
    }

    [Test]
    public void NearbyProtectionBoundary_IsExclusiveAndDeterministic()
    {
        LostItemCandidate item = Eligible();
        item.NearestPlayerDistanceSquared = 100 * 100;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).ShouldCollect,
            Is.True);
        item.NearestPlayerDistanceSquared = 99.99f * 99.99f;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).Reason,
            Is.EqualTo("another-player-nearby"));
    }

    [Test]
    public void OwnerDistanceAndGraceBoundaries_AreDeterministic()
    {
        LostItemCandidate item = Eligible();
        item.OwnerDistanceSquared = 200 * 200;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).Kind,
            Is.EqualTo(LostItemDecisionKind.Collect));
        item.OwnerDistanceSquared = 199 * 199;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).Reason,
            Is.EqualTo("owner-nearby"));
        item.OwnerDistanceSquared = 201 * 201;
        item.EligibleDurationSeconds = 9.99f;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).Kind,
            Is.EqualTo(LostItemDecisionKind.WaitingForGrace));
        item.EligibleDurationSeconds = 10f;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).Kind,
            Is.EqualTo(LostItemDecisionKind.Collect));
    }

    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    public void InteractionAndContainmentPreventCollection(bool grabbed, bool snapped, bool machine)
    {
        LostItemCandidate item = Eligible();
        item.IsGrabbed = grabbed;
        item.IsSnapped = snapped;
        item.IsInMachine = machine;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).Reason,
            Is.EqualTo("actively-contained-or-interacted"));
    }

    [Test]
    public void UnknownPositionsAndActiveContainment_FailSafe()
    {
        LostItemCandidate item = Eligible();
        item.OwnerPositionKnown = false;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).ShouldCollect, Is.False);
        item.OwnerPositionKnown = true;
        item.NearestPlayerPositionKnown = false;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).Reason,
            Is.EqualTo("player-positions-unknown"));
        item.NearestPlayerPositionKnown = true;
        item.IsGrabbed = true;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).ShouldCollect, Is.False);
    }

    [Test]
    public void LicensePaper_IsAlwaysExcluded()
    {
        LostItemCandidate item = Eligible();
        item.IsLicensePaper = true;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).Reason,
            Is.EqualTo("license-paper-excluded"));
    }

    [Test]
    public void WorldJunkThatPassedThroughInventory_IsNeverCollected()
    {
        LostItemCandidate item = Eligible();
        item.IsPersonalItem = false;
        Assert.That(LostItemCollectionPolicy.Evaluate(item, 200, 100, 10).Reason,
            Is.EqualTo("not-a-personal-item"));
    }

    [Test]
    public void Registry_EnforcesOneCanonicalEntryAndOwner()
    {
        LostItemRegistry registry = new();
        LostItemRecord input = Record();
        Assert.That(registry.Add(input), Is.EqualTo(LostItemRegistryResult.Added));
        Assert.That(registry.Add(input), Is.EqualTo(LostItemRegistryResult.DuplicateNetId));
        Assert.That(registry.RemoveForRetrieval(42, 2, 7, out _), Is.EqualTo(LostItemRegistryResult.WrongOwner));
        Assert.That(registry.RemoveForRetrieval(42, 1, 6, out _), Is.EqualTo(LostItemRegistryResult.StaleRevision));
        Assert.That(registry.RemoveForRetrieval(42, 1, 7, out LostItemRecord removed), Is.EqualTo(LostItemRegistryResult.Removed));
        Assert.That(removed.NetId, Is.EqualTo(42));
        Assert.That(registry.Count, Is.Zero);
    }

    [Test]
    public void Registry_DetachesAndPreservesInventoryClaimMetadata()
    {
        LostItemRecord input = Record();
        input.InventoryClaimPlayerId = 1;
        input.InventoryClaimSlot = 3;
        input.InventoryClaimFlags = 5;
        LostItemRegistry registry = new();
        registry.Add(input);

        input.InventoryClaimSlot = 9;
        registry.TryGet(42, out LostItemRecord stored);

        Assert.That(stored.InventoryClaimPlayerId, Is.EqualTo(1));
        Assert.That(stored.InventoryClaimSlot, Is.EqualTo(3));
        Assert.That(stored.InventoryClaimFlags, Is.EqualTo(5));
    }

    [Test]
    public void Registry_AssignsCompactHandlesAndResolvesThemWithoutExposingPersistentId()
    {
        LostItemRegistry registry = new();
        LostItemRecord first = Record();
        LostItemRecord second = Record();
        second.NetId = 43;
        second.PersistentItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        Assert.That(registry.Add(first), Is.EqualTo(LostItemRegistryResult.Added));
        Assert.That(registry.Add(second), Is.EqualTo(LostItemRegistryResult.Added));
        registry.TryGet(42, out LostItemRecord storedFirst);
        registry.TryGet(43, out LostItemRecord storedSecond);

        Assert.That(storedFirst.Handle, Is.Not.Zero);
        Assert.That(storedSecond.Handle, Is.Not.Zero.And.Not.EqualTo(storedFirst.Handle));
        Assert.That(registry.TryGetByHandle(storedFirst.Handle, out LostItemRecord resolved), Is.True);
        Assert.That(resolved.PersistentItemId, Is.EqualTo(first.PersistentItemId));
        Assert.That(registry.TryGetByPersistentId(first.PersistentItemId, out LostItemRecord persistent),
            Is.True);
        Assert.That(persistent.NetId, Is.EqualTo(first.NetId));
    }

    [Test]
    public void Registry_RejectsDuplicatePersistentIdentityAcrossNetIds()
    {
        LostItemRegistry registry = new();
        LostItemRecord first = Record();
        LostItemRecord duplicate = Record();
        duplicate.NetId = 43;
        Assert.That(registry.Add(first), Is.EqualTo(LostItemRegistryResult.Added));
        Assert.That(registry.Add(duplicate),
            Is.EqualTo(LostItemRegistryResult.DuplicatePersistentItemId));
    }

    [TestCase(0, 1)]
    [TestCase(42, 0)]
    public void Registry_RejectsInvalidRuntimeOrOwnerIdentity(int netId, int owner)
    {
        LostItemRecord record = Record();
        record.NetId = (ushort)netId;
        record.OwnerPlayerId = (byte)owner;
        Assert.That(new LostItemRegistry().Add(record),
            Is.EqualTo(LostItemRegistryResult.Invalid));
    }

    [Test]
    public void Registry_OwnerSnapshotsArePrivateDetachedCopies()
    {
        LostItemRegistry registry = new();
        registry.Add(Record());
        LostItemRecord other = Record();
        other.NetId = 43;
        other.PersistentItemId = Guid.NewGuid();
        other.OwnerPlayerId = 2;
        registry.Add(other);

        IReadOnlyList<LostItemRecord> ownerOne = registry.GetForOwner(1);
        Assert.That(ownerOne, Has.Count.EqualTo(1));
        Assert.That(ownerOne[0].NetId, Is.EqualTo(42));
        ownerOne[0].DisplayName = "tampered";
        registry.TryGet(42, out LostItemRecord canonical);
        Assert.That(canonical.DisplayName, Is.EqualTo("Radio"));
    }

    [Test]
    public void Registry_RemovalInvalidatesBothCompactAndDurableIndexes()
    {
        LostItemRegistry registry = new();
        LostItemRecord record = Record();
        registry.Add(record);
        registry.TryGet(42, out LostItemRecord stored);

        Assert.That(registry.RemoveStale(42, out _),
            Is.EqualTo(LostItemRegistryResult.Removed));
        Assert.That(registry.TryGetByHandle(stored.Handle, out _), Is.False);
        Assert.That(registry.TryGetByPersistentId(stored.PersistentItemId, out _), Is.False);
    }

    [Test]
    public void Registry_RestoreAllocatesRuntimeHandleInsteadOfPersistingOne()
    {
        LostItemRegistry registry = new();
        LostItemRecord restored = Record();
        restored.Handle = 0;

        Assert.That(registry.Restore(restored), Is.EqualTo(LostItemRegistryResult.Updated));
        Assert.That(registry.TryGet(restored.NetId, out LostItemRecord stored), Is.True);
        Assert.That(stored.Handle, Is.Not.Zero);
        Assert.That(registry.TryGetByHandle(stored.Handle, out LostItemRecord resolved), Is.True);
        Assert.That(resolved.PersistentItemId, Is.EqualTo(restored.PersistentItemId));
    }

    [Test]
    public void Registry_ExternalCanonicalTransitionInvalidatesOldEntryWithoutOldRevision()
    {
        LostItemRegistry registry = new();
        registry.Add(Record());

        Assert.That(registry.RemoveStale(42, out LostItemRecord removed),
            Is.EqualTo(LostItemRegistryResult.Removed));
        Assert.That(removed.NetId, Is.EqualTo(42));
        Assert.That(removed.Revision, Is.EqualTo(7));
        Assert.That(registry.Count, Is.Zero);
        Assert.That(registry.RemoveStale(42, out _), Is.EqualTo(LostItemRegistryResult.NotFound));
    }

    [Test]
    public void Retrieval_PreservesRegistryWhenInventoryIsFull()
    {
        LostItemRegistry registry = new();
        registry.Add(Record());
        registry.TryGet(42, out LostItemRecord stored);
        LostItemRetrievalPlan plan = LostItemRetrievalPlanner.Plan(stored, 1, stored.Revision,
            -1, -1, 2, new[] { 0, 1 });
        Assert.That(plan.Accepted, Is.False);
        Assert.That(plan.Reason, Is.EqualTo("inventory-full"));
        Assert.That(registry.Count, Is.EqualTo(1));
    }

    [Test]
    public void Retrieval_SelectsRequestedOrFirstFreeSlot()
    {
        LostItemRecord record = Record();
        record.Revision = 8;
        Assert.That(LostItemRetrievalPlanner.Plan(record, 1, 8, 3, -1, 5, new[] { 0, 1 }).TargetSlot,
            Is.EqualTo(3));
        Assert.That(LostItemRetrievalPlanner.Plan(record, 1, 8, 1, -1, 5, new[] { 0, 1 }).TargetSlot,
            Is.EqualTo(2));
    }

    [Test]
    public void Retrieval_RevivesExistingSilhouetteWithoutTryingToMoveIt()
    {
        LostItemRecord record = Record();
        record.InventoryClaimSlot = 5;
        LostItemRetrievalPlan plan = LostItemRetrievalPlanner.Plan(record, 1, 7,
            -1, 3, 10, new[] { 0, 1, 3 });
        Assert.That(plan.Accepted, Is.True);
        Assert.That(plan.TargetSlot, Is.EqualTo(3));
    }

    [Test]
    public void Retrieval_ExistingSilhouetteWinsEvenWhenRequestedSlotIsFree()
    {
        LostItemRetrievalPlan plan = LostItemRetrievalPlanner.Plan(Record(), 1, 7,
            8, 3, 10, new[] { 0, 1, 3 });
        Assert.That(plan.Accepted, Is.True);
        Assert.That(plan.TargetSlot, Is.EqualTo(3));
    }

    [TestCase(2, 7, 2, "requester-not-persistent-owner")]
    [TestCase(1, 6, 2, "stale-lost-item-revision")]
    [TestCase(1, 7, 0, "inventory-unavailable")]
    public void Retrieval_RejectsInvalidAuthorityOrInventory(byte player, int revision,
        int capacity, string expectedReason)
    {
        LostItemRetrievalPlan plan = LostItemRetrievalPlanner.Plan(Record(), player, (uint)revision,
            -1, -1, capacity, new int[0]);
        Assert.That(plan.Accepted, Is.False);
        Assert.That(plan.Reason, Is.EqualTo(expectedReason));
    }

    private static LostItemRecord Record() => new()
    {
        PersistentItemId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        NetId = 42,
        Revision = 7,
        OwnerPlayerId = 1,
        PrefabName = "CommsRadio",
        DisplayName = "Radio"
    };
}
