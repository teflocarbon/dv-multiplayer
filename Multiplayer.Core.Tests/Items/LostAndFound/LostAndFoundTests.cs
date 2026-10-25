using System;
using System.Collections.Generic;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.LostAndFound;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.LostAndFound;

[TestFixture]
public sealed class LostAndFoundTests
{
    private static LostItemCandidate Eligible() => new()
    {
        NetId = 42,
        Revision = 7,
        Owner = new Guid(1, 0, 0, new byte[8]),
        OwnerConnected = true,
        Placement = LivePlacement.World,
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

    [TestCase(LivePlacement.PlayerHand)]
    [TestCase(LivePlacement.PlayerInventory)]
    [TestCase(LivePlacement.Container)]
    [TestCase(LivePlacement.Attached)]
    [TestCase(LivePlacement.Installed)]
    [TestCase(LivePlacement.LostAndFound)]
    public void NonWorldPlacement_IsNeverCollected(LivePlacement placement)
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
    public void Registry_EnforcesOneCanonicalEntryAndOwner()
    {
        LostItemRegistry registry = new();
        LostItemRecord input = Record();
        Assert.That(registry.Add(input), Is.EqualTo(LostItemRegistryResult.Added));
        Assert.That(registry.Add(input), Is.EqualTo(LostItemRegistryResult.DuplicateNetId));
        Assert.That(registry.RemoveForRetrieval(42, O(2), 7, out _), Is.EqualTo(LostItemRegistryResult.WrongOwner));
        Assert.That(registry.RemoveForRetrieval(42, O(1), 6, out _), Is.EqualTo(LostItemRegistryResult.StaleRevision));
        Assert.That(registry.RemoveForRetrieval(42, O(1), 7, out LostItemRecord removed), Is.EqualTo(LostItemRegistryResult.Removed));
        Assert.That(removed.NetId, Is.EqualTo(42));
        Assert.That(registry.Count, Is.Zero);
    }

    [Test]
    public void Registry_DetachesAndPreservesInventoryClaimMetadata()
    {
        LostItemRecord input = Record();
        input.InventoryClaimSlot = 3;
        input.InventoryClaimFlags = 5;
        LostItemRegistry registry = new();
        registry.Add(input);

        input.InventoryClaimSlot = 9;
        registry.TryGet(42, out LostItemRecord stored);

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
        record.OwnerIdentity = owner == 0 ? string.Empty : O(owner);
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
        other.OwnerIdentity = O(2);
        registry.Add(other);

        IReadOnlyList<LostItemRecord> ownerOne = registry.GetForOwner(O(1));
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
        LostItemRetrievalPlan plan = LostItemRetrievalPlanner.Plan(stored, O(1), stored.Revision,
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
        Assert.That(LostItemRetrievalPlanner.Plan(record, O(1), 8, 3, -1, 5, new[] { 0, 1 }).TargetSlot,
            Is.EqualTo(3));
        Assert.That(LostItemRetrievalPlanner.Plan(record, O(1), 8, 1, -1, 5, new[] { 0, 1 }).TargetSlot,
            Is.EqualTo(2));
    }

    [Test]
    public void Retrieval_RevivesExistingSilhouetteWithoutTryingToMoveIt()
    {
        LostItemRecord record = Record();
        record.InventoryClaimSlot = 5;
        LostItemRetrievalPlan plan = LostItemRetrievalPlanner.Plan(record, O(1), 7,
            -1, 3, 10, new[] { 0, 1, 3 });
        Assert.That(plan.Accepted, Is.True);
        Assert.That(plan.TargetSlot, Is.EqualTo(3));
    }

    [Test]
    public void Retrieval_ExistingSilhouetteWinsEvenWhenRequestedSlotIsFree()
    {
        LostItemRetrievalPlan plan = LostItemRetrievalPlanner.Plan(Record(), O(1), 7,
            8, 3, 10, new[] { 0, 1, 3 });
        Assert.That(plan.Accepted, Is.True);
        Assert.That(plan.TargetSlot, Is.EqualTo(3));
    }

    [TestCase(2, 7, 2, "requester-not-persistent-owner")]
    [TestCase(1, 6, 2, "stale-lost-item-revision")]
    [TestCase(1, 7, 0, "inventory-unavailable")]
    public void Retrieval_RejectsInvalidAuthorityOrInventory(int player, int revision,
        int capacity, string expectedReason)
    {
        LostItemRetrievalPlan plan = LostItemRetrievalPlanner.Plan(Record(), O(player), (uint)revision,
            -1, -1, capacity, new int[0]);
        Assert.That(plan.Accepted, Is.False);
        Assert.That(plan.Reason, Is.EqualTo(expectedReason));
    }

    [Test]
    public void AnOwnersEntries_AreTheirsWhetherOrNotTheyAreConnected()
    {
        // An offline owner is an identity with no session: nothing unbinds when they leave or
        // rebinds when they come back (item-refactor.md, "Identity only").
        LostItemRegistry registry = new();
        registry.Add(Record());

        Assert.That(registry.GetForOwner(O(1)), Has.Count.EqualTo(1));
        Assert.That(registry.GetForOwner(O(1).ToUpperInvariant()), Has.Count.EqualTo(1), "identities compare without case");
        Assert.That(registry.RemoveForRetrieval(42, O(3), 7, out _), Is.EqualTo(LostItemRegistryResult.WrongOwner));
        Assert.That(registry.RemoveForRetrieval(42, O(1), 7, out _), Is.EqualTo(LostItemRegistryResult.Removed));
    }

    [Test]
    public void GetForOwner_ListsOnlyThatIdentitysEntries()
    {
        LostItemRegistry registry = new();
        LostItemRecord theirs = Record();
        theirs.NetId = 43;
        theirs.PersistentItemId = Guid.NewGuid();
        theirs.OwnerIdentity = O(2);
        registry.Add(Record());
        registry.Add(theirs);

        Assert.That(registry.GetForOwner(O(1)), Has.Count.EqualTo(1));
        Assert.That(registry.GetForOwner(O(2))[0].NetId, Is.EqualTo(43));
        Assert.That(registry.GetForOwner(string.Empty), Is.Empty);
        Assert.That(registry.GetForOwner(O(5)), Is.Empty);
    }

    [Test]
    public void RekeyOwner_MovesOnlyThatOwnersRecords()
    {
        LostItemRegistry registry = new();
        LostItemRecord mine = Record();
        mine.OwnerIdentity = "owner-a";
        LostItemRecord theirs = Record();
        theirs.NetId = 43;
        theirs.PersistentItemId = Guid.NewGuid();
        theirs.OwnerIdentity = "owner-b";
        registry.Add(mine);
        registry.Add(theirs);

        Assert.That(registry.RekeyOwner("OWNER-A", "owner-c"), Is.EqualTo(1), "identities compare without case");
        Assert.That(registry.HasOwner("owner-a"), Is.False);
        Assert.That(registry.HasOwner("owner-c"), Is.True);
        Assert.That(registry.HasOwner("owner-b"), Is.True);
        Assert.That(registry.RekeyOwner("owner-a", string.Empty), Is.Zero);
    }

    [Test]
    public void RekeyItem_ARemintedCopyIsFoundByItsNewId_AndTheOldIdIsFree()
    {
        // A copy in Lost and Found re-minted by the one-home repair (D7): before, its record kept the
        // old id, so it was saved under neither and the old id's lookups returned the copy.
        LostItemRegistry registry = new();
        LostItemRecord copy = Record();
        Guid from = copy.PersistentItemId, fresh = Guid.NewGuid();
        registry.Add(copy);

        Assert.That(registry.RekeyItem(copy.NetId, fresh), Is.True);

        Assert.That(registry.TryGetByPersistentId(fresh, out LostItemRecord found) && found.NetId == copy.NetId, Is.True);
        Assert.That(registry.TryGet(copy.NetId, out LostItemRecord now) ? now.PersistentItemId : Guid.Empty, Is.EqualTo(fresh));
        Assert.That(registry.TryGetByPersistentId(from, out _), Is.False);
        LostItemRecord original = Record();
        original.NetId = 43;
        Assert.That(registry.Add(original), Is.EqualTo(LostItemRegistryResult.Added), "the old id is the original's to take");

        Assert.That(registry.RekeyItem(43, fresh), Is.False, "another item's id");
        Assert.That(registry.RekeyItem(99, Guid.NewGuid()), Is.False, "no record");
    }

    [TestCase(null)]
    [TestCase(LivePlacement.LostAndFound)]
    [TestCase(LivePlacement.World)]
    public void Reconcile_EntryWhoseItemIsGone_IsDestroyed(LivePlacement? placement)
    {
        Assert.That(LostItemEntryReconciler.Classify(itemPresent: false, placement),
            Is.EqualTo(LostItemEntryStatus.ItemDestroyed));
    }

    [Test]
    public void Reconcile_StoredItemIsKept()
    {
        Assert.That(LostItemEntryReconciler.Classify(true, LivePlacement.LostAndFound),
            Is.EqualTo(LostItemEntryStatus.Stored));
    }

    [Test]
    public void Reconcile_LiveItemWithoutAuthorityRecordIsLeftAlone()
    {
        Assert.That(LostItemEntryReconciler.Classify(true, null),
            Is.EqualTo(LostItemEntryStatus.AwaitingAuthority));
    }

    [TestCase(LivePlacement.World)]
    [TestCase(LivePlacement.PlayerInventory)]
    [TestCase(LivePlacement.PlayerHand)]
    [TestCase(LivePlacement.Destroyed)]
    public void Reconcile_ItemMovedOutByAnotherSystem_PlacementChanged(LivePlacement placement)
    {
        Assert.That(LostItemEntryReconciler.Classify(true, placement),
            Is.EqualTo(LostItemEntryStatus.PlacementChanged));
    }

    [Test]
    public void Reconcile_DestroyedJobBookletLeavesOwnersListAndItsHandleStopsResolving()
    {
        // A collected job booklet the job system destroyed when its job ended: its item and
        // authority record are gone, the radio beside it is still stored.
        LostItemRegistry registry = new();
        LostItemRecord radio = Record();
        LostItemRecord booklet = Record();
        booklet.NetId = 739;
        booklet.PersistentItemId = Guid.NewGuid();
        booklet.PrefabName = "JobBooklet";
        registry.Add(radio);
        registry.Add(booklet);
        registry.TryGet(739, out LostItemRecord storedBooklet);
        Dictionary<ushort, LivePlacement> live = new() { [42] = LivePlacement.LostAndFound };

        foreach (LostItemRecord entry in registry.GetAll())
        {
            bool present = live.TryGetValue(entry.NetId, out LivePlacement placement);
            if (LostItemEntryReconciler.Classify(present, present ? placement : null) ==
                LostItemEntryStatus.ItemDestroyed)
                registry.RemoveStale(entry.NetId, out _);
        }

        Assert.That(registry.GetForOwner(O(1)), Has.Count.EqualTo(1));
        Assert.That(registry.GetForOwner(O(1))[0].NetId, Is.EqualTo(42));
        Assert.That(registry.TryGetByHandle(storedBooklet.Handle, out _), Is.False);
    }

    private static LostItemRecord Record() => new()
    {
        PersistentItemId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        NetId = 42,
        Revision = 7,
        OwnerIdentity = O(1),
        PrefabName = "CommsRadio",
        DisplayName = "Radio"
    };

    [TestCase(LivePlacement.Attached)]
    [TestCase(LivePlacement.SnappedAttachment)]
    [TestCase(LivePlacement.Magazine)]
    public void ARefusedCollectionOffACouplerASnapPointOrAMagazineGoesIntoTheWorld(LivePlacement previous) =>
        Assert.That(LostItemCollectionPolicy.RollsBackIntoWorld(previous), Is.True);

    [TestCase(LivePlacement.World)]
    [TestCase(LivePlacement.TrainInterior)]
    [TestCase(LivePlacement.StaticParent)]
    public void ARefusedCollectionFromWhereItLayStaysThere(LivePlacement previous) =>
        Assert.That(LostItemCollectionPolicy.RollsBackIntoWorld(previous), Is.False);

    [TestCase(LivePlacement.Attached, LivePlacement.World, true)]
    [TestCase(LivePlacement.SnappedAttachment, LivePlacement.World, true)]   // a deleted car's hanger: once, not twice
    [TestCase(LivePlacement.Magazine, LivePlacement.World, true)]
    [TestCase(LivePlacement.SnappedAttachment, LivePlacement.SnappedAttachment, false)]
    [TestCase(LivePlacement.TrainInterior, LivePlacement.World, false)]
    public void ADeletedCarsItemTheRollbackPutInTheWorld_IsntPutThereAgain(LivePlacement before, LivePlacement now, bool already) =>
        Assert.That(LostItemCollectionPolicy.RolledBackIntoWorld(before, now), Is.EqualTo(already));

    // Owners are identities, numbered here for short.
    private static string O(int number) => new Guid(number, 0, 0, new byte[8]).ToString("D");
}
