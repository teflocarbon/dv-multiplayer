using System;
using System.Collections.Generic;
using Multiplayer.Core.Containers;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Containers;

[TestFixture]
public sealed class ColdContainerGraphTests
{
    private const string Owner = "owner-a";
    private ColdContainerGraph graph;
    private Guid root;

    [SetUp]
    public void SetUp()
    {
        graph = new ColdContainerGraph();
        root = Guid.NewGuid();
        Assert.That(graph.AddContainer(Container(root, Owner, 4)).Accepted, Is.True);
    }

    [Test]
    public void UnauthorizedViewer_CannotDiscoverContents()
    {
        Deposit(root, 0);

        ContainerOperationResult result = graph.Browse("intruder", root, 0, 64, out var view);

        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.Unauthorized));
        Assert.That(view, Is.Null);
    }

    [Test]
    public void OwnerBrowse_ReturnsDetachedRowsWithoutNetIds()
    {
        ColdStoredItemRecord item = Item();
        Deposit(root, 0, item, physicalNetId: 812);
        graph.FinalizeDepositRetirement(Guid.NewGuid(), item.PersistentItemId);

        ContainerOperationResult result = graph.Browse(Owner, root, 0, 64, out var view);

        Assert.That(result.Accepted, Is.True);
        Assert.That(view.Slots, Has.Count.EqualTo(1));
        Assert.That(view.Slots[0].PrefabName, Is.EqualTo(item.PrefabName));
        Assert.That(view.Slots[0].ItemHandle, Is.Not.Zero);
    }

    [Test]
    public void Deposit_CommitsColdAuthorityBeforeRetirement()
    {
        ColdStoredItemRecord item = Item();
        Guid operation = Guid.NewGuid();

        ContainerOperationResult result = graph.CommitDeposit(new DepositCommand
        {
            OperationId = operation,
            ActorIdentity = Owner,
            DestinationContainerId = root,
            ExpectedDestinationRevision = 0,
            DestinationSlot = 1,
            PhysicalNetId = 700,
            Item = item
        });

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.ItemState,
            Is.EqualTo(ColdItemLifecycleState.ColdCommittedPendingRetirement));
        Assert.That(graph.TryGetItem(item.PersistentItemId, out var stored), Is.True);
        Assert.That(stored.RetiringNetId, Is.EqualTo(700));
        Assert.That(graph.TryGetContainer(root, out var container), Is.True);
        Assert.That(container.Revision, Is.EqualTo(1));
    }

    [Test]
    public void DepositRetry_IsIdempotentAndDoesNotAdvanceRevisionTwice()
    {
        ColdStoredItemRecord item = Item();
        Guid operation = Guid.NewGuid();
        DepositCommand command = DepositCommand(operation, root, 0, item);

        ContainerOperationResult first = graph.CommitDeposit(command);
        ContainerOperationResult retry = graph.CommitDeposit(command);

        Assert.That(retry.Status, Is.EqualTo(first.Status));
        Assert.That(retry.DestinationRevision, Is.EqualTo(1));
        Assert.That(graph.ItemCount, Is.EqualTo(1));
        graph.TryGetContainer(root, out var container);
        Assert.That(container.Revision, Is.EqualTo(1));
    }

    [Test]
    public void FailedDeposit_LeavesGraphUntouched()
    {
        Deposit(root, 0);
        ColdStoredItemRecord second = Item();

        ContainerOperationResult result = graph.CommitDeposit(DepositCommand(
            Guid.NewGuid(), root, 1, second, slot: 0));

        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.SlotOccupied));
        Assert.That(graph.ItemCount, Is.EqualTo(1));
        graph.TryGetContainer(root, out var container);
        Assert.That(container.Revision, Is.EqualTo(1));
    }

    [Test]
    public void StaleDepositRevision_IsRejectedWithoutMutation()
    {
        ColdStoredItemRecord item = Item();

        ContainerOperationResult result = graph.CommitDeposit(DepositCommand(
            Guid.NewGuid(), root, expectedRevision: 9, item));

        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.StaleRevision));
        Assert.That(graph.ItemCount, Is.Zero);
    }

    [Test]
    public void Withdrawal_DoesNotRemoveColdEdgeUntilMaterializedCommit()
    {
        ColdStoredItemRecord item = Item();
        Deposit(root, 0, item);
        graph.FinalizeDepositRetirement(Guid.NewGuid(), item.PersistentItemId);
        graph.TryGetContainer(root, out var container);
        Guid operation = Guid.NewGuid();
        WithdrawalCommand command = new()
        {
            OperationId = operation,
            ActorIdentity = Owner,
            SourceContainerId = root,
            ExpectedSourceRevision = container.Revision,
            SourceSlot = 0
        };

        ContainerOperationResult prepared = graph.PrepareWithdrawal(command, out var staged);
        graph.Browse(Owner, root, 0, 64, out var beforeCommit);
        ContainerOperationResult materialized = graph.MarkWithdrawalMaterialized(operation);
        ContainerOperationResult committed = graph.CommitWithdrawal(operation);
        graph.Browse(Owner, root, 0, 64, out var afterCommit);

        Assert.That(prepared.Accepted, Is.True);
        Assert.That(staged.LifecycleState, Is.EqualTo(ColdItemLifecycleState.PreparingWithdrawal));
        Assert.That(beforeCommit.Slots, Has.Count.EqualTo(1));
        Assert.That(materialized.ItemState,
            Is.EqualTo(ColdItemLifecycleState.MaterializedPendingCommit));
        Assert.That(committed.ItemState, Is.EqualTo(ColdItemLifecycleState.Materialized));
        Assert.That(afterCommit.Slots, Is.Empty);
        Assert.That(graph.ItemCount, Is.Zero);
    }

    [Test]
    public void WithdrawalFailure_LeavesColdRecordAndRevisionUntouched()
    {
        ColdStoredItemRecord item = Item();
        Deposit(root, 0, item);
        graph.FinalizeDepositRetirement(Guid.NewGuid(), item.PersistentItemId);
        graph.TryGetContainer(root, out var container);
        Guid operation = Guid.NewGuid();
        graph.PrepareWithdrawal(new WithdrawalCommand
        {
            OperationId = operation,
            ActorIdentity = Owner,
            SourceContainerId = root,
            ExpectedSourceRevision = container.Revision,
            SourceSlot = 0
        }, out _);

        graph.AbortWithdrawal(operation, "materialization-failed");

        graph.TryGetItem(item.PersistentItemId, out var restored);
        graph.TryGetContainer(root, out var unchanged);
        Assert.That(restored.LifecycleState, Is.EqualTo(ColdItemLifecycleState.Cold));
        Assert.That(restored.ParentContainerId, Is.EqualTo(root));
        Assert.That(unchanged.Revision, Is.EqualTo(container.Revision));
    }

    [Test]
    public void ConcurrentWithdrawal_IsRejectedWhileFirstOperationOwnsReservation()
    {
        ColdStoredItemRecord item = Item();
        Deposit(root, 0, item);
        graph.FinalizeDepositRetirement(Guid.NewGuid(), item.PersistentItemId);
        graph.TryGetContainer(root, out var container);
        graph.PrepareWithdrawal(Withdraw(Guid.NewGuid(), root, container.Revision), out _);

        ContainerOperationResult result = graph.PrepareWithdrawal(
            Withdraw(Guid.NewGuid(), root, container.Revision), out _);

        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.OperationConflict));
    }

    [Test]
    public void MoveBetweenContainers_AdvancesBothRevisionsAtomically()
    {
        Guid other = Guid.NewGuid();
        graph.AddContainer(Container(other, Owner, 4));
        ColdStoredItemRecord item = Item();
        Deposit(root, 0, item);
        graph.FinalizeDepositRetirement(Guid.NewGuid(), item.PersistentItemId);
        graph.TryGetContainer(root, out var source);
        graph.TryGetContainer(other, out var destination);

        ContainerOperationResult result = graph.Move(new MoveCommand
        {
            OperationId = Guid.NewGuid(),
            ActorIdentity = Owner,
            SourceContainerId = root,
            ExpectedSourceRevision = source.Revision,
            SourceSlot = 0,
            DestinationContainerId = other,
            ExpectedDestinationRevision = destination.Revision,
            DestinationSlot = 2
        });

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.SourceRevision, Is.EqualTo(source.Revision + 1));
        Assert.That(result.DestinationRevision, Is.EqualTo(destination.Revision + 1));
        graph.TryGetItem(item.PersistentItemId, out var moved);
        Assert.That(moved.ParentContainerId, Is.EqualTo(other));
        Assert.That(moved.Slot, Is.EqualTo(2));
    }

    [Test]
    public void NestedMove_CannotPlaceContainerInsideItsDescendant()
    {
        Guid child = Guid.NewGuid();
        graph.AddContainer(Container(child, Owner, 4));
        ColdStoredItemRecord childShell = Item(childContainerId: child);
        Deposit(root, 0, childShell);
        graph.FinalizeDepositRetirement(Guid.NewGuid(), childShell.PersistentItemId);
        graph.TryGetContainer(root, out var rootRecord);
        graph.TryGetContainer(child, out var childRecord);

        ColdStoredItemRecord rootShell = Item(childContainerId: root);
        ContainerOperationResult result = graph.CommitDeposit(new DepositCommand
        {
            OperationId = Guid.NewGuid(),
            ActorIdentity = Owner,
            DestinationContainerId = child,
            ExpectedDestinationRevision = childRecord.Revision,
            DestinationSlot = 0,
            Item = rootShell
        });

        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.CycleDetected));
        Assert.That(graph.ItemCount, Is.EqualTo(1));
    }

    [Test]
    public void CompatibilityPolicy_IsHostControlled()
    {
        ColdContainerGraph rejecting = new(compatibility: new RejectAllCompatibility());
        Guid container = Guid.NewGuid();
        rejecting.AddContainer(Container(container, Owner, 4));

        ContainerOperationResult result = rejecting.CommitDeposit(DepositCommand(
            Guid.NewGuid(), container, 0, Item()));

        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.Incompatible));
    }

    [Test]
    public void OwnerQuota_IsEnforced()
    {
        ColdContainerGraph limited = new(new ColdContainerLimits
        {
            MaximumItemsPerOwner = 1,
            MaximumCapacity = 4
        });
        Guid container = Guid.NewGuid();
        limited.AddContainer(Container(container, Owner, 4));
        Assert.That(limited.CommitDeposit(DepositCommand(Guid.NewGuid(), container, 0,
            Item(), slot: 0)).Accepted, Is.True);
        limited.TryGetContainer(container, out var revision);

        ContainerOperationResult second = limited.CommitDeposit(DepositCommand(Guid.NewGuid(),
            container, revision.Revision, Item(), slot: 1));

        Assert.That(second.Status, Is.EqualTo(ContainerOperationStatus.OwnerQuotaExceeded));
    }

    [Test]
    public void DetachedStateBudget_IsEnforcedBeforeLogicalCommit()
    {
        ColdStoredItemRecord item = Item();
        item.DetachedState = new byte[33];
        ColdContainerGraph limited = new(new ColdContainerLimits
        {
            MaximumCapacity = 4,
            MaximumDetachedStateBytes = 32
        });
        limited.AddContainer(Container(root, Owner, 4));

        ContainerOperationResult result = limited.CommitDeposit(DepositCommand(Guid.NewGuid(),
            root, 0, item));

        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.InvalidRecord));
        Assert.That(limited.ItemCount, Is.Zero);
    }

    [Test]
    public void RevisionExhaustion_RejectsMutationInsteadOfReusingRevision()
    {
        Guid exhausted = Guid.NewGuid();
        graph.AddContainer(new ColdContainerRecord
        {
            PersistentContainerId = exhausted,
            OwnerIdentity = Owner,
            Capacity = 1,
            Revision = uint.MaxValue
        });

        ContainerOperationResult result = graph.CommitDeposit(DepositCommand(Guid.NewGuid(),
            exhausted, uint.MaxValue, Item()));

        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.InvalidState));
        Assert.That(graph.ItemCount, Is.Zero);
    }

    [Test]
    public void SaveRoundTrip_PreservesLogicalStateAndNormalizesRuntimeRetirement()
    {
        ColdStoredItemRecord item = Item();
        item.DetachedState = new byte[] { 1, 2, 3 };
        Deposit(root, 0, item, physicalNetId: 801);
        ColdContainerGraphSnapshot snapshot = graph.ExportSnapshot();
        ColdContainerGraph loaded = new();

        ContainerOperationResult result = loaded.ImportSnapshot(snapshot);

        Assert.That(result.Accepted, Is.True);
        Assert.That(loaded.TryGetItem(item.PersistentItemId, out var restored), Is.True);
        Assert.That(restored.PersistentOwnerIdentity, Is.EqualTo(item.PersistentOwnerIdentity));
        Assert.That(restored.DetachedState, Is.EqualTo(item.DetachedState));
        Assert.That(restored.LifecycleState, Is.EqualTo(ColdItemLifecycleState.Cold));
        Assert.That(restored.RetiringNetId, Is.Zero);
    }

    [Test]
    public void MalformedSaveWithDuplicateSlot_IsRejectedWithoutReplacingCurrentGraph()
    {
        ColdStoredItemRecord first = Item();
        ColdStoredItemRecord second = Item();
        ColdContainerGraphSnapshot malformed = new()
        {
            Containers = new List<ColdContainerRecord> { Container(root, Owner, 4) },
            Items = new List<ColdStoredItemRecord>
            {
                Stored(first, root, 0),
                Stored(second, root, 0)
            }
        };

        ContainerOperationResult result = graph.ImportSnapshot(malformed);

        Assert.That(result.Accepted, Is.False);
        Assert.That(graph.ContainerCount, Is.EqualTo(1));
        Assert.That(graph.ItemCount, Is.Zero);
    }

    [Test]
    public void SnapshotWithForeignOwnedEdge_IsRejected()
    {
        ColdStoredItemRecord foreign = Stored(Item(owner: "owner-b"), root, 0);
        ColdContainerGraphSnapshot malformed = new()
        {
            Containers = new List<ColdContainerRecord> { Container(root, Owner, 4) },
            Items = new List<ColdStoredItemRecord> { foreign }
        };

        ContainerOperationResult result = graph.ImportSnapshot(malformed);

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.InvalidRecord));
        Assert.That(graph.ItemCount, Is.Zero);
    }

    [Test]
    public void ForeignOwnedItem_CannotBeDepositedIntoPrivateContainer()
    {
        ColdStoredItemRecord foreign = Item(owner: "owner-b");
        ContainerOperationResult result = graph.CommitDeposit(DepositCommand(Guid.NewGuid(),
            root, expectedRevision: 0, foreign));

        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.Unauthorized));
        Assert.That(result.Reason, Is.EqualTo("deposit-item-not-owned"));
        Assert.That(graph.ItemCount, Is.Zero);
        Assert.That(graph.TryGetContainer(root, out var container), Is.True);
        Assert.That(container.Revision, Is.Zero);
    }

    private void Deposit(Guid container, int slot, ColdStoredItemRecord item = null,
        ushort physicalNetId = 0)
    {
        graph.TryGetContainer(container, out var current);
        ContainerOperationResult result = graph.CommitDeposit(DepositCommand(Guid.NewGuid(),
            container, current.Revision, item ?? Item(), slot, physicalNetId));
        Assert.That(result.Accepted, Is.True, result.Reason);
    }

    private static DepositCommand DepositCommand(Guid operation, Guid container,
        uint expectedRevision, ColdStoredItemRecord item, int slot = 0, ushort physicalNetId = 0) => new()
    {
        OperationId = operation,
        ActorIdentity = Owner,
        DestinationContainerId = container,
        ExpectedDestinationRevision = expectedRevision,
        DestinationSlot = slot,
        PhysicalNetId = physicalNetId,
        Item = item
    };

    private static WithdrawalCommand Withdraw(Guid operation, Guid container, uint revision) => new()
    {
        OperationId = operation,
        ActorIdentity = Owner,
        SourceContainerId = container,
        ExpectedSourceRevision = revision,
        SourceSlot = 0
    };

    private static ColdContainerRecord Container(Guid id, string owner, int capacity) => new()
    {
        PersistentContainerId = id,
        OwnerIdentity = owner,
        Capacity = capacity
    };

    private static ColdStoredItemRecord Item(string owner = Owner, Guid? childContainerId = null) => new()
    {
        PersistentItemId = Guid.NewGuid(),
        PrefabName = "TestItem",
        DisplayName = "Test Item",
        PersistentOwnerIdentity = owner,
        ChildContainerId = childContainerId,
        StateVersion = 1
    };

    private static ColdStoredItemRecord Stored(ColdStoredItemRecord item, Guid parent, int slot)
    {
        ColdStoredItemRecord clone = item.Clone();
        clone.ParentContainerId = parent;
        clone.Slot = slot;
        return clone;
    }

    private sealed class RejectAllCompatibility : IColdContainerCompatibilityPolicy
    {
        public bool IsCompatible(ColdStoredItemRecord item, ColdContainerRecord destination) => false;
    }
}
