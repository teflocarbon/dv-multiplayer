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
    private TestOwners owners;
    private ContainerGraph graph;
    private Guid root;

    [SetUp]
    public void SetUp()
    {
        owners = new TestOwners();
        graph = new ContainerGraph(owners: owners);
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
        StoredItemRecord childShell = Item();
        Guid child = childShell.PersistentItemId;
        graph.AddContainer(Container(child, Owner, 4));
        Deposit(root, 0, childShell);
        graph.FinalizeDepositRetirement(Guid.NewGuid(), childShell.PersistentItemId);
        graph.TryGetContainer(child, out var childRecord);

        StoredItemRecord rootShell = Item(id: root);
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
    public void AStoredShell_OpensTheContainerWithItsId()
    {
        // OI-CON-09: the container's id is its shell's item id, so nothing can mint another.
        StoredItemRecord shell = Item();
        graph.AddContainer(Container(shell.PersistentItemId, Owner, 4));
        Deposit(root, 1, shell);
        graph.TryGetContainer(shell.PersistentItemId, out var inner);

        graph.Browse(Owner, root, 0, 64, out var view);

        Assert.That(graph.IsContainer(shell.PersistentItemId), Is.True);
        Assert.That(view.Slots.Single().IsContainer, Is.True);
        Assert.That(view.Slots.Single().ChildContainerHandle, Is.EqualTo(inner.SessionHandle));
        // Stored once: the same shell can't go into a second slot.
        graph.TryGetContainer(root, out var current);
        Assert.That(graph.CommitDeposit(DepositCommand(Guid.NewGuid(), root, current.Revision, shell, slot: 2)).Status,
            Is.EqualTo(ContainerOperationStatus.DuplicateIdentity));
    }

    [Test]
    public void AContainerInsideItself_IsRefusedAtImport()
    {
        StoredItemRecord self = Stored(Item(id: root), root, 0);

        IReadOnlyList<ContainerRowRefusal> refused = graph.ImportRows(new[] { Container(root, Owner, 4) }, new[] { self });

        Assert.That(refused.Single().Reason, Is.EqualTo("container-cycle-detected"));
        Assert.That(graph.ItemCount, Is.Zero);
    }

    [Test]
    public void CompatibilityPolicy_IsHostControlled()
    {
        ContainerGraph rejecting = new(compatibility: new RejectAllCompatibility(), owners: owners);
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
        }, owners: owners);
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
        }, owners: owners);
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
        owners.Own(exhausted, Owner);
        graph.AddContainer(new ContainerRecord
        {
            PersistentContainerId = exhausted,
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
        ContainerGraph loaded = new(owners: owners);

        IReadOnlyList<ContainerRowRefusal> refused = loaded.ImportRows(snapshot.Containers, snapshot.Items);

        Assert.That(refused, Is.Empty);
        Assert.That(loaded.TryGetItem(item.PersistentItemId, out var restored), Is.True);
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
    public void AForeignOwnedRow_LoadsAndShowsAsSomeoneElses()
    {
        // Rows stand alone and owners are the ledger's: a row has no owner to disagree with.
        StoredItemRecord foreign = Stored(Item(owner: "owner-b"), root, 0);

        IReadOnlyList<ContainerRowRefusal> refused = graph.ImportRows(new[] { Container(root, Owner, 4) }, new[] { foreign });
        graph.Browse(Owner, root, 0, 64, out var view);

        Assert.That(refused, Is.Empty);
        Assert.That(view.Slots.Single().ForeignOwned, Is.True);
    }

    [Test]
    public void ARowNestedTooDeep_IsRefused()
    {
        ContainerGraph shallow = new(new ContainerLimits { MaximumDepth = 1 }, owners: owners);
        Guid outer = Guid.NewGuid(), middle = Guid.NewGuid(), inner = Guid.NewGuid();
        // A container's id is its shell's item id.
        StoredItemRecord middleShell = Stored(Item(id: middle), outer, 0);
        StoredItemRecord innerShell = Stored(Item(id: inner), middle, 0);

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
    public void AnUnownedItem_CannotBeDeposited()
    {
        ContainerOperationResult result = graph.CommitDeposit(DepositCommand(Guid.NewGuid(),
            root, expectedRevision: 0, Item(owner: string.Empty)));

        Assert.That(result.Reason, Is.EqualTo("deposit-item-not-owned"));
        Assert.That(graph.ItemCount, Is.Zero);
    }

    [Test]
    public void TheOwnerIsTheLedgers_SoARekeyNeedsNothingFromTheGraph()
    {
        StoredItemRecord item = Item();
        Deposit(root, 0, item);

        owners.Own(root, "owner-c");
        owners.Own(item.PersistentItemId, "owner-c");

        Assert.That(graph.Browse(Owner, root, 0, 64, out _).Status, Is.EqualTo(ContainerOperationStatus.Unauthorized));
        Assert.That(graph.Browse("owner-c", root, 0, 64, out var view).Accepted, Is.True);
        Assert.That(view.Slots.Single().ForeignOwned, Is.False);
        Assert.That(graph.ValidateWholeGraph().Accepted, Is.True);
    }

    [Test]
    public void AnUnownedShell_IsKeptByWhoeverHoldsIt()
    {
        // OI-CON-08: nobody owns the box; its holder opens it, and the next holder after them.
        Guid box = Guid.NewGuid();
        graph.AddContainer(Container(box, string.Empty, 4));
        owners.Hold(box, Owner);

        Assert.That(graph.Browse(Owner, box, 0, 64, out _).Accepted, Is.True);
        Assert.That(graph.Browse("owner-b", box, 0, 64, out _).Status, Is.EqualTo(ContainerOperationStatus.Unauthorized));
        owners.Hold(box, "owner-b");
        Assert.That(graph.KeeperOf(box), Is.EqualTo("owner-b"));
        Assert.That(graph.Browse("owner-b", box, 0, 64, out _).Accepted, Is.True);
    }

    [Test]
    public void AnUnownedBoxInsideAnother_IsKeptByTheOuterBoxesKeeper()
    {
        StoredItemRecord inner = Item(owner: string.Empty);
        graph.ImportRows(new[] { Container(root, Owner, 4), Container(inner.PersistentItemId, string.Empty, 4) },
            new[] { Stored(inner, root, 0) });

        Assert.That(graph.KeeperOf(inner.PersistentItemId), Is.EqualTo(Owner));
        Assert.That(graph.Browse(Owner, inner.PersistentItemId, 0, 64, out _).Accepted, Is.True);
    }

    [Test]
    public void CheckDeposit_ChangesNothing()
    {
        StoredItemRecord item = Item();
        DepositCommand command = DepositCommand(Guid.NewGuid(), root, 0, item);

        ContainerOperationResult checkedFirst = graph.CheckDeposit(command);

        Assert.That(checkedFirst.Accepted, Is.True);
        Assert.That(graph.ItemCount, Is.Zero);
        graph.TryGetContainer(root, out var container);
        Assert.That(container.Revision, Is.Zero);
        Assert.That(graph.CommitDeposit(command).Accepted, Is.True, "the check didn't remember the operation");
    }

    [Test]
    public void RevokeDeposit_TakesTheItemBackOutAndARepeatIsRefused()
    {
        // OI-CON-06: an object that couldn't be retired stays live, and the box doesn't keep a copy.
        StoredItemRecord item = Item();
        Guid operation = Guid.NewGuid();
        DepositCommand command = DepositCommand(operation, root, 0, item);
        graph.CommitDeposit(command);

        ContainerOperationResult revoked = graph.RevokeDeposit(operation, item.PersistentItemId, "deposit-retirement-failed");

        Assert.That(revoked.Reason, Is.EqualTo("deposit-retirement-failed"));
        Assert.That(graph.TryGetItem(item.PersistentItemId, out _), Is.False);
        Assert.That(graph.CommitDeposit(command).Accepted, Is.False);
        Assert.That(graph.RevokeDeposit(operation, item.PersistentItemId, "again").Reason,
            Is.EqualTo("item-not-pending-retirement"));
    }

    [Test]
    public void ABoxPastTheDepthLimit_IsRefusedAtDeposit_AndOneAtItLoadsAgain()
    {
        // OI-CON-15: a deposit holds the graph to the limit the load holds a save to.
        graph = new ContainerGraph(new ContainerLimits { MaximumDepth = 2 }, owners: owners);
        graph.AddContainer(Container(root, Owner, 4));
        StoredItemRecord first = Box(), second = Box(), third = Box();
        Deposit(root, 0, first);
        Deposit(first.PersistentItemId, 0, second);
        graph.TryGetContainer(second.PersistentItemId, out var deepest);

        ContainerOperationResult tooDeep = graph.CommitDeposit(DepositCommand(Guid.NewGuid(),
            second.PersistentItemId, deepest.Revision, third));

        Assert.That(tooDeep.Status, Is.EqualTo(ContainerOperationStatus.DepthExceeded));
        Assert.That(tooDeep.Reason, Is.EqualTo("container-depth-exceeded"));
        ContainerGraphSnapshot saved = graph.ExportSnapshot();
        Assert.That(new ContainerGraph(new ContainerLimits { MaximumDepth = 2 }, owners: owners)
            .ImportRows(saved.Containers, saved.Items), Is.Empty);
    }

    [Test]
    public void ABoxWithBoxesInside_CountsTheirDepthAtDeposit()
    {
        graph = new ContainerGraph(new ContainerLimits { MaximumDepth = 2 }, owners: owners);
        graph.AddContainer(Container(root, Owner, 4));
        StoredItemRecord outer = Box(), inner = Box(), level = Box();
        Deposit(root, 0, level);
        Deposit(outer.PersistentItemId, 0, inner);
        graph.TryGetContainer(level.PersistentItemId, out var destination);

        // level is 1 deep; outer would be 2 and inner 3.
        ContainerOperationResult result = graph.CommitDeposit(DepositCommand(Guid.NewGuid(),
            level.PersistentItemId, destination.Revision, outer));

        Assert.That(result.Status, Is.EqualTo(ContainerOperationStatus.DepthExceeded));
        Assert.That(graph.CommitDeposit(DepositCommand(Guid.NewGuid(),
            root, Revision(root), outer, slot: 1)).Accepted, Is.True, "two deep under the outermost fits");
    }

    // An empty box: a shell owned by Owner whose container has its id.
    private StoredItemRecord Box()
    {
        StoredItemRecord shell = Item();
        graph.AddContainer(Container(shell.PersistentItemId, Owner, 4));
        return shell;
    }

    private uint Revision(Guid container) =>
        graph.TryGetContainer(container, out var record) ? record.Revision : 0;

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

    // A container, its shell owned in the ledger by owner (nobody when empty).
    private ContainerRecord Container(Guid id, string owner, int capacity)
    {
        owners.Own(id, owner);
        return new ContainerRecord { PersistentContainerId = id, Capacity = capacity };
    }

    // An item owned in the ledger by owner (nobody when empty).
    private StoredItemRecord Item(string owner = Owner, Guid? id = null)
    {
        StoredItemRecord item = new()
        {
            PersistentItemId = id ?? Guid.NewGuid(),
            PrefabName = "TestItem",
            DisplayName = "Test Item",
            StateVersion = 1
        };
        owners.Own(item.PersistentItemId, owner);
        return item;
    }

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

    // The ledger's owners and the live shells' holders, as the server's oracle gives them.
    private sealed class TestOwners : IContainerOwners
    {
        private readonly Dictionary<Guid, string> owned = new();
        private readonly Dictionary<Guid, string> held = new();

        public void Own(Guid id, string owner) => owned[id] = owner ?? string.Empty;
        public void Hold(Guid id, string holder) => held[id] = holder ?? string.Empty;
        public string OwnerOf(Guid itemId) => owned.TryGetValue(itemId, out string owner) ? owner : string.Empty;
        public string HolderOf(Guid containerId) => held.TryGetValue(containerId, out string holder) ? holder : string.Empty;
    }
}
