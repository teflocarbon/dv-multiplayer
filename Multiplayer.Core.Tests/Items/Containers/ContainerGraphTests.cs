using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.Containers;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Containers;

[TestFixture]
public sealed class ContainerGraphTests
{
    private const string Owner = "owner-a";
    private ContainerGraph graph;
    private Guid root;

    [SetUp]
    public void SetUp()
    {
        graph = new ContainerGraph();
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
        StoredItemRecord item = Item();
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
        StoredItemRecord item = Item();
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
            Is.EqualTo(ItemLifecycleState.ColdCommittedPendingRetirement));
        Assert.That(graph.TryGetItem(item.PersistentItemId, out var stored), Is.True);
        Assert.That(stored.RetiringNetId, Is.EqualTo(700));
        Assert.That(graph.TryGetContainer(root, out var container), Is.True);
        Assert.That(container.Revision, Is.EqualTo(1));
    }

    [Test]
    public void DepositRetry_IsIdempotentAndDoesNotAdvanceRevisionTwice()
    {
        StoredItemRecord item = Item();
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
        StoredItemRecord second = Item();

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
        StoredItemRecord item = Item();

        ContainerOperationResult result = graph.CommitDeposit(DepositCommand(
            Guid.NewGuid(), root, expectedRevision: 9, item));

        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.StaleRevision));
        Assert.That(graph.ItemCount, Is.Zero);
    }

    [Test]
    public void Withdrawal_DoesNotRemoveColdEdgeUntilMaterializedCommit()
    {
        StoredItemRecord item = Item();
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
        Assert.That(staged.LifecycleState, Is.EqualTo(ItemLifecycleState.PreparingWithdrawal));
        Assert.That(beforeCommit.Slots, Has.Count.EqualTo(1));
        Assert.That(materialized.ItemState,
            Is.EqualTo(ItemLifecycleState.MaterializedPendingCommit));
        Assert.That(committed.ItemState, Is.EqualTo(ItemLifecycleState.Materialized));
        Assert.That(afterCommit.Slots, Is.Empty);
        Assert.That(graph.ItemCount, Is.Zero);
    }

    [Test]
    public void WithdrawalFailure_LeavesColdRecordAndRevisionUntouched()
    {
        StoredItemRecord item = Item();
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
        Assert.That(restored.LifecycleState, Is.EqualTo(ItemLifecycleState.Cold));
        Assert.That(restored.ParentContainerId, Is.EqualTo(root));
        Assert.That(unchanged.Revision, Is.EqualTo(container.Revision));
    }

    [Test]
    public void ConcurrentWithdrawal_IsRejectedWhileFirstOperationOwnsReservation()
    {
        StoredItemRecord item = Item();
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
        StoredItemRecord item = Item();
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
        StoredItemRecord childShell = Item(childContainerId: child);
        Deposit(root, 0, childShell);
        graph.FinalizeDepositRetirement(Guid.NewGuid(), childShell.PersistentItemId);
        graph.TryGetContainer(root, out var rootRecord);
        graph.TryGetContainer(child, out var childRecord);

        StoredItemRecord rootShell = Item(childContainerId: root);
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
        ContainerGraph rejecting = new(compatibility: new RejectAllCompatibility());
        Guid container = Guid.NewGuid();
        rejecting.AddContainer(Container(container, Owner, 4));

        ContainerOperationResult result = rejecting.CommitDeposit(DepositCommand(
            Guid.NewGuid(), container, 0, Item()));

        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.Incompatible));
    }

    [Test]
    public void OwnerQuota_IsEnforced()
    {
        ContainerGraph limited = new(new ContainerLimits
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
        StoredItemRecord item = Item();
        item.DetachedState = new byte[33];
        ContainerGraph limited = new(new ContainerLimits
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
        graph.AddContainer(new ContainerRecord
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
        StoredItemRecord item = Item();
        item.DetachedState = new byte[] { 1, 2, 3 };
        Deposit(root, 0, item, physicalNetId: 801);
        ContainerGraphSnapshot snapshot = graph.ExportSnapshot();
        ContainerGraph loaded = new();

        IReadOnlyList<ContainerRowRefusal> refused = loaded.ImportRows(snapshot.Containers, snapshot.Items);

        Assert.That(refused, Is.Empty);
        Assert.That(loaded.TryGetItem(item.PersistentItemId, out var restored), Is.True);
        Assert.That(restored.PersistentOwnerIdentity, Is.EqualTo(item.PersistentOwnerIdentity));
        Assert.That(restored.DetachedState, Is.EqualTo(item.DetachedState));
        Assert.That(restored.LifecycleState, Is.EqualTo(ItemLifecycleState.Cold));
        Assert.That(restored.RetiringNetId, Is.Zero);
    }

    [Test]
    public void ARowInATakenSlot_IsRefusedAlone()
    {
        StoredItemRecord first = Item();
        StoredItemRecord second = Item();
        StoredItemRecord third = Item();

        IReadOnlyList<ContainerRowRefusal> refused = graph.ImportRows(new[] { Container(root, Owner, 4) },
            new[] { Stored(first, root, 0), Stored(second, root, 0), Stored(third, root, 1) });

        Assert.That(refused.Select(r => r.Item?.PersistentItemId), Is.EqualTo(new Guid?[] { second.PersistentItemId }));
        Assert.That(refused.Single().Reason, Is.EqualTo("container-slot-occupied"));
        Assert.That(graph.ItemCount, Is.EqualTo(2));
    }

    [Test]
    public void ARowPastItsContainersEnd_IsRefusedAndTheRestLoad()
    {
        // F18: a capacity change left one item's slot past the end. Every other row still loads.
        StoredItemRecord past = Item();
        StoredItemRecord fine = Item();

        IReadOnlyList<ContainerRowRefusal> refused = graph.ImportRows(new[] { Container(root, Owner, 4) },
            new[] { Stored(past, root, 9), Stored(fine, root, 2) });

        Assert.That(refused.Single().Item.PersistentItemId, Is.EqualTo(past.PersistentItemId));
        Assert.That(refused.Single().Reason, Is.EqualTo("container-slot-invalid"));
        Assert.That(graph.TryGetItem(fine.PersistentItemId, out _), Is.True);
    }

    [Test]
    public void AForeignOwnedRow_IsRefused()
    {
        StoredItemRecord foreign = Stored(Item(owner: "owner-b"), root, 0);

        IReadOnlyList<ContainerRowRefusal> refused = graph.ImportRows(new[] { Container(root, Owner, 4) }, new[] { foreign });

        Assert.That(refused.Single().Reason, Is.EqualTo("container-foreign-owned-item"));
        Assert.That(graph.ItemCount, Is.Zero);
        Assert.That(graph.ContainerCount, Is.EqualTo(1));
    }

    [Test]
    public void ARowNestedTooDeep_IsRefused()
    {
        ContainerGraph shallow = new(new ContainerLimits { MaximumDepth = 1 });
        Guid outer = Guid.NewGuid(), middle = Guid.NewGuid(), inner = Guid.NewGuid();
        StoredItemRecord middleShell = Stored(Item(), outer, 0);
        middleShell.ChildContainerId = middle;
        StoredItemRecord innerShell = Stored(Item(), middle, 0);
        innerShell.ChildContainerId = inner;

        IReadOnlyList<ContainerRowRefusal> refused = shallow.ImportRows(
            new[] { Container(outer, Owner, 4), Container(middle, Owner, 4), Container(inner, Owner, 4) },
            new[] { middleShell, innerShell });

        Assert.That(refused.Single().Item.PersistentItemId, Is.EqualTo(innerShell.PersistentItemId));
        Assert.That(refused.Single().Reason, Is.EqualTo("container-depth-exceeded"));
        Assert.That(shallow.TryGetItem(middleShell.PersistentItemId, out _), Is.True);
    }

    [Test]
    public void ForeignOwnedItem_CannotBeDepositedIntoPrivateContainer()
    {
        StoredItemRecord foreign = Item(owner: "owner-b");
        ContainerOperationResult result = graph.CommitDeposit(DepositCommand(Guid.NewGuid(),
            root, expectedRevision: 0, foreign));

        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.Unauthorized));
        Assert.That(result.Reason, Is.EqualTo("deposit-item-not-owned"));
        Assert.That(graph.ItemCount, Is.Zero);
        Assert.That(graph.TryGetContainer(root, out var container), Is.True);
        Assert.That(container.Revision, Is.Zero);
    }

    [Test]
    public void RekeyOwner_MovesContainersAndTheirItemsTogether()
    {
        Deposit(root, 0);
        Guid other = Guid.NewGuid();
        Assert.That(graph.AddContainer(Container(other, "owner-b", 4)).Accepted, Is.True);

        Assert.That(graph.RekeyOwner(Owner, "owner-c"), Is.EqualTo(2));

        Assert.That(graph.HasOwner(Owner), Is.False);
        Assert.That(graph.HasOwner("owner-c"), Is.True);
        Assert.That(graph.Browse("owner-c", root, 0, 64, out var view).Accepted, Is.True);
        Assert.That(view.Slots, Has.Count.EqualTo(1));
        Assert.That(graph.TryGetContainer(other, out var untouched) && untouched.OwnerIdentity == "owner-b", Is.True);
        Assert.That(graph.ValidateWholeGraph().Accepted, Is.True);
        Assert.That(graph.RekeyOwner(string.Empty, "owner-c"), Is.Zero);
    }

    [Test]
    public void SnapshotRekey_SurvivesImport()
    {
        Deposit(root, 0);
        ContainerGraphSnapshot snapshot = graph.ExportSnapshot();

        Assert.That(snapshot.RekeyOwner(Owner, "owner-c"), Is.EqualTo(2));

        ContainerGraph imported = new();
        Assert.That(imported.ImportRows(snapshot.Containers, snapshot.Items), Is.Empty);
        Assert.That(imported.HasOwner("owner-c"), Is.True);
        Assert.That(imported.HasOwner(Owner), Is.False);
    }

    private void Deposit(Guid container, int slot, StoredItemRecord item = null,
        ushort physicalNetId = 0)
    {
        graph.TryGetContainer(container, out var current);
        ContainerOperationResult result = graph.CommitDeposit(DepositCommand(Guid.NewGuid(),
            container, current.Revision, item ?? Item(), slot, physicalNetId));
        Assert.That(result.Accepted, Is.True, result.Reason);
    }

    private static DepositCommand DepositCommand(Guid operation, Guid container,
        uint expectedRevision, StoredItemRecord item, int slot = 0, ushort physicalNetId = 0) => new()
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

    private static ContainerRecord Container(Guid id, string owner, int capacity) => new()
    {
        PersistentContainerId = id,
        OwnerIdentity = owner,
        Capacity = capacity
    };

    private static StoredItemRecord Item(string owner = Owner, Guid? childContainerId = null) => new()
    {
        PersistentItemId = Guid.NewGuid(),
        PrefabName = "TestItem",
        DisplayName = "Test Item",
        PersistentOwnerIdentity = owner,
        ChildContainerId = childContainerId,
        StateVersion = 1
    };

    private static StoredItemRecord Stored(StoredItemRecord item, Guid parent, int slot)
    {
        StoredItemRecord clone = item.Clone();
        clone.ParentContainerId = parent;
        clone.Slot = slot;
        return clone;
    }

    private sealed class RejectAllCompatibility : IContainerCompatibilityPolicy
    {
        public bool IsCompatible(StoredItemRecord item, ContainerRecord destination) => false;
    }
}
