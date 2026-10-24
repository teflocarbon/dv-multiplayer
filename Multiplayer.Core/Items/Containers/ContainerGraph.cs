using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Items.Containers;

public enum ItemLifecycleState : byte
{
    Cold,
    ColdCommittedPendingRetirement,
    PreparingWithdrawal,
    MaterializedPendingCommit,
    Materialized
}

public enum ContainerOperationKind : byte
{
    Browse,
    Deposit,
    Withdraw,
    Move
}

public enum ContainerOperationStatus : byte
{
    Accepted,
    Unauthorized,
    NotFound,
    StaleRevision,
    SlotOutOfRange,
    SlotOccupied,
    Incompatible,
    DuplicateIdentity,
    AlreadyParented,
    CycleDetected,
    DepthExceeded,
    NodeBudgetExceeded,
    OwnerQuotaExceeded,
    OperationConflict,
    InvalidState,
    InvalidRecord
}

public sealed class ContainerLimits
{
    public int MaximumDepth { get; set; } = 8;
    public int MaximumVisitedNodes { get; set; } = 256;
    public int MaximumItemsPerOwner { get; set; } = 4096;
    public int MaximumCapacity { get; set; } = 256;
    public int MaximumDetachedStateBytes { get; set; } = 512 * 1024;

    public ContainerLimits Clone() => new()
    {
        MaximumDepth = MaximumDepth,
        MaximumVisitedNodes = MaximumVisitedNodes,
        MaximumItemsPerOwner = MaximumItemsPerOwner,
        MaximumCapacity = MaximumCapacity,
        MaximumDetachedStateBytes = MaximumDetachedStateBytes
    };
}

/// <summary>
/// A cold container. Its id is its shell's item id (docs/design/items/containers.md): a container
/// is stored inside another when the graph holds an item with that id.
/// </summary>
public sealed class ContainerRecord
{
    public Guid PersistentContainerId { get; set; }
    public string PrefabName { get; set; } = string.Empty;
    public int Capacity { get; set; }
    public uint Revision { get; set; }
    public uint SessionHandle { get; set; }

    public ContainerRecord Clone() => new()
    {
        PersistentContainerId = PersistentContainerId,
        PrefabName = PrefabName,
        Capacity = Capacity,
        Revision = Revision,
        SessionHandle = SessionHandle
    };
}

public sealed class StoredItemRecord
{
    public Guid PersistentItemId { get; set; }
    public string PrefabName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AuthoredItemKey { get; set; } = string.Empty;
    public Guid ParentContainerId { get; set; }
    public int Slot { get; set; } = -1;
    public uint StateVersion { get; set; }
    public byte[] DetachedState { get; set; } = Array.Empty<byte>();
    public ItemLifecycleState LifecycleState { get; set; } = ItemLifecycleState.Cold;
    public ushort RetiringNetId { get; set; }
    public uint SessionHandle { get; set; }

    public StoredItemRecord Clone() => new()
    {
        PersistentItemId = PersistentItemId,
        PrefabName = PrefabName,
        DisplayName = DisplayName,
        AuthoredItemKey = AuthoredItemKey,
        ParentContainerId = ParentContainerId,
        Slot = Slot,
        StateVersion = StateVersion,
        DetachedState = DetachedState?.ToArray() ?? Array.Empty<byte>(),
        LifecycleState = LifecycleState,
        RetiringNetId = RetiringNetId,
        SessionHandle = SessionHandle
    };
}

public sealed class ContainerSlotView
{
    public int Slot { get; set; }
    public uint ItemHandle { get; set; }
    public string PrefabName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool ForeignOwned { get; set; }
    public bool IsContainer { get; set; }
    public uint ChildContainerHandle { get; set; }
    public int ChildItemCount { get; set; }
    public uint StateVersion { get; set; }
}

public sealed class ContainerView
{
    public uint ContainerHandle { get; set; }
    public uint Revision { get; set; }
    public int Capacity { get; set; }
    public int Offset { get; set; }
    public bool HasMore { get; set; }
    public List<ContainerSlotView> Slots { get; set; } = new();
}

public sealed class ContainerOperationResult
{
    public Guid OperationId { get; set; }
    public ContainerOperationKind Kind { get; set; }
    public ContainerOperationStatus Status { get; set; }
    public string Reason { get; set; } = string.Empty;
    public uint SourceRevision { get; set; }
    public uint DestinationRevision { get; set; }
    public Guid ItemId { get; set; }
    public ItemLifecycleState ItemState { get; set; }
    public bool Accepted => Status == ContainerOperationStatus.Accepted;

    public ContainerOperationResult Clone() => (ContainerOperationResult)MemberwiseClone();
}

public sealed class DepositCommand
{
    public Guid OperationId { get; set; }
    public string ActorIdentity { get; set; } = string.Empty;
    public Guid DestinationContainerId { get; set; }
    public uint ExpectedDestinationRevision { get; set; }
    public int DestinationSlot { get; set; }
    public ushort PhysicalNetId { get; set; }
    public StoredItemRecord Item { get; set; }
}

public sealed class WithdrawalCommand
{
    public Guid OperationId { get; set; }
    public string ActorIdentity { get; set; } = string.Empty;
    public Guid SourceContainerId { get; set; }
    public uint ExpectedSourceRevision { get; set; }
    public int SourceSlot { get; set; }
}

public sealed class MoveCommand
{
    public Guid OperationId { get; set; }
    public string ActorIdentity { get; set; } = string.Empty;
    public Guid SourceContainerId { get; set; }
    public uint ExpectedSourceRevision { get; set; }
    public int SourceSlot { get; set; }
    public Guid DestinationContainerId { get; set; }
    public uint ExpectedDestinationRevision { get; set; }
    public int DestinationSlot { get; set; }
}

public interface IContainerCompatibilityPolicy
{
    bool IsCompatible(StoredItemRecord item, ContainerRecord destination);
}

public sealed class AllowAllContainerCompatibility : IContainerCompatibilityPolicy
{
    public bool IsCompatible(StoredItemRecord item, ContainerRecord destination) => true;
}

/// <summary>
/// Who owns an item and who holds a shell: the ledger's and the live items', never the graph's
/// (docs/design/items/containers.md, "Owners"). Identities are strings, empty for nobody.
/// </summary>
public interface IContainerOwners
{
    /// <summary>The item's owner, or empty when nobody owns it.</summary>
    string OwnerOf(Guid itemId);
    /// <summary>Who holds the container's shell as a live item (in a hand or an inventory), or empty.</summary>
    string HolderOf(Guid containerId);
}

/// <summary>Nobody owns or holds anything: a graph with no ledger behind it.</summary>
public sealed class NoContainerOwners : IContainerOwners
{
    public string OwnerOf(Guid itemId) => string.Empty;
    public string HolderOf(Guid containerId) => string.Empty;
}

/// <summary>A saved container row, or a stored item's, that <see cref="ContainerGraph.ImportRows"/> refused, and why.</summary>
public sealed class ContainerRowRefusal
{
    public ContainerRowRefusal(ContainerRecord container, StoredItemRecord item, string reason)
    {
        Container = container;
        Item = item;
        Reason = reason ?? string.Empty;
    }

    public ContainerRecord Container { get; }
    public StoredItemRecord Item { get; }
    public string Reason { get; }
}

/// <summary>What the graph holds, copied: for the owned-items list, the debug overlay and the save.</summary>
public sealed class ContainerGraphSnapshot
{
    public int Version { get; set; } = 2;
    public List<ContainerRecord> Containers { get; set; } = new();
    public List<StoredItemRecord> Items { get; set; } = new();
}

/// <summary>
/// Pure host-authoritative graph. Logical commits are atomic; Unity representation cleanup is
/// deliberately represented as a follow-up lifecycle state and never rolled back here.
/// </summary>
public sealed class ContainerGraph
{
    private readonly Dictionary<Guid, ContainerRecord> containers = new();
    private readonly Dictionary<Guid, StoredItemRecord> items = new();
    private readonly Dictionary<Guid, ContainerOperationResult> completedOperations = new();
    private readonly Queue<Guid> completedOperationOrder = new();
    private readonly Dictionary<Guid, Guid> preparingWithdrawals = new();
    private readonly IContainerCompatibilityPolicy compatibility;
    private readonly IContainerOwners owners;
    private uint nextSessionHandle = 1;

    public ContainerGraph(ContainerLimits limits = null,
        IContainerCompatibilityPolicy compatibility = null, IContainerOwners owners = null)
    {
        Limits = (limits ?? new ContainerLimits()).Clone();
        this.compatibility = compatibility ?? new AllowAllContainerCompatibility();
        this.owners = owners ?? new NoContainerOwners();
    }

    public ContainerLimits Limits { get; }
    public int ContainerCount => containers.Count;
    public int ItemCount => items.Count;

    public ContainerOperationResult AddContainer(ContainerRecord record)
    {
        if (record == null || record.PersistentContainerId == Guid.Empty || record.Capacity <= 0 ||
            record.Capacity > Limits.MaximumCapacity)
            return Reject(Guid.Empty, ContainerOperationKind.Deposit,
                ContainerOperationStatus.InvalidRecord, "invalid-container-record");
        if (containers.ContainsKey(record.PersistentContainerId))
            return Reject(Guid.Empty, ContainerOperationKind.Deposit,
                ContainerOperationStatus.DuplicateIdentity, "duplicate-container-id");
        ContainerRecord clone = record.Clone();
        clone.SessionHandle = AllocateHandle();
        containers.Add(clone.PersistentContainerId, clone);
        return Accept(Guid.Empty, ContainerOperationKind.Deposit, destinationRevision: clone.Revision);
    }

    public bool TryGetContainer(Guid id, out ContainerRecord record)
    {
        bool found = containers.TryGetValue(id, out ContainerRecord raw);
        record = found ? raw.Clone() : null;
        return found;
    }

    public bool TryGetContainer(uint handle, out ContainerRecord record)
    {
        ContainerRecord raw = containers.Values.FirstOrDefault(value => value.SessionHandle == handle);
        record = raw?.Clone();
        return raw != null;
    }

    public bool TryGetItem(Guid id, out StoredItemRecord record)
    {
        bool found = items.TryGetValue(id, out StoredItemRecord raw);
        record = found ? raw.Clone() : null;
        return found;
    }

    public bool TryGetItem(uint handle, out StoredItemRecord record)
    {
        StoredItemRecord raw = items.Values.FirstOrDefault(value => value.SessionHandle == handle);
        record = raw?.Clone();
        return raw != null;
    }

    public ContainerOperationResult Browse(string actorIdentity, Guid containerId,
        int offset, int count, out ContainerView view)
    {
        view = null;
        if (!containers.TryGetValue(containerId, out ContainerRecord container))
            return Reject(Guid.Empty, ContainerOperationKind.Browse,
                ContainerOperationStatus.NotFound, "container-not-found");
        if (!Authorized(actorIdentity, container))
            return Reject(Guid.Empty, ContainerOperationKind.Browse,
                ContainerOperationStatus.Unauthorized, "container-private");
        int boundedOffset = Math.Max(0, offset);
        int boundedCount = Math.Max(1, Math.Min(count, 64));
        List<StoredItemRecord> direct = items.Values
            .Where(item => item.ParentContainerId == containerId &&
                item.LifecycleState != ItemLifecycleState.Materialized)
            .OrderBy(item => item.Slot).ToList();
        view = new ContainerView
        {
            ContainerHandle = container.SessionHandle,
            Revision = container.Revision,
            Capacity = container.Capacity,
            Offset = boundedOffset,
            HasMore = direct.Count > boundedOffset + boundedCount,
            Slots = direct.Skip(boundedOffset).Take(boundedCount).Select(item =>
            {
                ContainerRecord child = containers.TryGetValue(item.PersistentItemId, out ContainerRecord found)
                    ? found : null;
                return new ContainerSlotView
                {
                    Slot = item.Slot,
                    ItemHandle = item.SessionHandle,
                    PrefabName = item.PrefabName,
                    DisplayName = item.DisplayName,
                    ForeignOwned = OwnerOf(item.PersistentItemId) is { Length: > 0 } owner &&
                        !string.Equals(owner, actorIdentity, StringComparison.Ordinal),
                    IsContainer = child != null,
                    ChildContainerHandle = child?.SessionHandle ?? 0,
                    ChildItemCount = child == null ? 0 : items.Values.Count(value =>
                        value.ParentContainerId == child.PersistentContainerId &&
                        value.LifecycleState != ItemLifecycleState.Materialized),
                    StateVersion = item.StateVersion
                };
            }).ToList()
        };
        return Accept(Guid.Empty, ContainerOperationKind.Browse,
            sourceRevision: container.Revision);
    }

    /// <summary>
    /// Whether the deposit would be committed, changing nothing: the server asks first, moves the
    /// ledger, then commits (docs/design/items/containers.md, "Deposit"). A repeated operation's
    /// remembered result is returned as it was.
    /// </summary>
    public ContainerOperationResult CheckDeposit(DepositCommand command)
    {
        if (command == null)
            return Reject(Guid.Empty, ContainerOperationKind.Deposit,
                ContainerOperationStatus.InvalidRecord, "deposit-command-null");
        if (TryCompleted(command.OperationId, out ContainerOperationResult completed))
            return completed;
        if (!containers.TryGetValue(command.DestinationContainerId, out ContainerRecord destination))
            return Reject(command.OperationId, ContainerOperationKind.Deposit,
                ContainerOperationStatus.NotFound, "destination-not-found");
        ContainerOperationResult validation = ValidateDestination(command.OperationId,
            ContainerOperationKind.Deposit, command.ActorIdentity, destination,
            command.ExpectedDestinationRevision, command.DestinationSlot, command.Item,
            ContainerOf(command.Item));
        if (!validation.Accepted)
            return validation;
        if (command.Item == null || command.Item.PersistentItemId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.Item.PrefabName) ||
            (command.Item.DetachedState?.Length ?? 0) > Limits.MaximumDetachedStateBytes)
            return Reject(command.OperationId, ContainerOperationKind.Deposit,
                ContainerOperationStatus.InvalidRecord, "invalid-item-record");
        // A deposit never changes ownership: only the depositor's own items go in (OI-CON-01).
        string owner = OwnerOf(command.Item.PersistentItemId);
        if (owner.Length == 0 || !string.Equals(owner, command.ActorIdentity, StringComparison.Ordinal))
            return Reject(command.OperationId, ContainerOperationKind.Deposit,
                ContainerOperationStatus.Unauthorized, "deposit-item-not-owned");
        // A container already stored in another is this too: its id is its shell's.
        if (items.ContainsKey(command.Item.PersistentItemId))
            return Reject(command.OperationId, ContainerOperationKind.Deposit,
                ContainerOperationStatus.DuplicateIdentity, "duplicate-item-id");
        if (CountOwnedItems(owner) >= Limits.MaximumItemsPerOwner)
            return Reject(command.OperationId, ContainerOperationKind.Deposit,
                ContainerOperationStatus.OwnerQuotaExceeded, "owner-item-quota-exceeded");
        if (CountKeptItems(KeeperOf(destination.PersistentContainerId)) >= Limits.MaximumItemsPerOwner)
            return Reject(command.OperationId, ContainerOperationKind.Deposit,
                ContainerOperationStatus.OwnerQuotaExceeded, "container-owner-item-quota-exceeded");
        return Accept(command.OperationId, ContainerOperationKind.Deposit,
            destinationRevision: destination.Revision, itemId: command.Item.PersistentItemId);
    }

    public ContainerOperationResult CommitDeposit(DepositCommand command)
    {
        if (command != null && TryCompleted(command.OperationId, out ContainerOperationResult completed))
            return completed;
        ContainerOperationResult check = CheckDeposit(command);
        if (!check.Accepted)
            return command == null ? check : Complete(command.OperationId, check);
        ContainerRecord destination = containers[command.DestinationContainerId];
        StoredItemRecord item = command.Item.Clone();
        item.ParentContainerId = destination.PersistentContainerId;
        item.Slot = command.DestinationSlot;
        item.LifecycleState = ItemLifecycleState.ColdCommittedPendingRetirement;
        item.RetiringNetId = command.PhysicalNetId;
        item.SessionHandle = AllocateHandle();
        items.Add(item.PersistentItemId, item);
        destination.Revision = NextRevision(destination.Revision);
        return Complete(command.OperationId, Accept(command.OperationId,
            ContainerOperationKind.Deposit, destinationRevision: destination.Revision,
            itemId: item.PersistentItemId, state: item.LifecycleState));
    }

    /// <summary>
    /// A committed deposit whose object couldn't be retired is taken back (OI-CON-06): the item
    /// leaves the container and stays the live object it was, and a repeat of the operation is
    /// refused with the reason.
    /// </summary>
    public ContainerOperationResult RevokeDeposit(Guid operationId, Guid itemId, string reason)
    {
        if (!items.TryGetValue(itemId, out StoredItemRecord item) ||
            item.LifecycleState != ItemLifecycleState.ColdCommittedPendingRetirement)
            return Reject(operationId, ContainerOperationKind.Deposit,
                ContainerOperationStatus.InvalidState, "item-not-pending-retirement");
        items.Remove(itemId);
        if (containers.TryGetValue(item.ParentContainerId, out ContainerRecord destination))
            destination.Revision = NextRevision(destination.Revision);
        return Complete(operationId, Reject(operationId, ContainerOperationKind.Deposit,
            ContainerOperationStatus.InvalidState, reason ?? "deposit-revoked"));
    }

    public ContainerOperationResult FinalizeDepositRetirement(Guid operationId, Guid itemId)
    {
        if (!items.TryGetValue(itemId, out StoredItemRecord item))
            return Reject(operationId, ContainerOperationKind.Deposit,
                ContainerOperationStatus.NotFound, "item-not-found");
        if (item.LifecycleState == ItemLifecycleState.Cold)
            return Accept(operationId, ContainerOperationKind.Deposit,
                itemId: itemId, state: item.LifecycleState);
        if (item.LifecycleState != ItemLifecycleState.ColdCommittedPendingRetirement)
            return Reject(operationId, ContainerOperationKind.Deposit,
                ContainerOperationStatus.InvalidState, "item-not-pending-retirement");
        item.LifecycleState = ItemLifecycleState.Cold;
        item.RetiringNetId = 0;
        return Accept(operationId, ContainerOperationKind.Deposit,
            itemId: itemId, state: item.LifecycleState);
    }

    public ContainerOperationResult PrepareWithdrawal(WithdrawalCommand command,
        out StoredItemRecord stagedRecord)
    {
        stagedRecord = null;
        if (command == null)
            return Reject(Guid.Empty, ContainerOperationKind.Withdraw,
                ContainerOperationStatus.InvalidRecord, "withdraw-command-null");
        if (TryCompleted(command.OperationId, out ContainerOperationResult completed))
        {
            if (completed.Accepted && items.TryGetValue(completed.ItemId, out StoredItemRecord existing))
                stagedRecord = existing.Clone();
            return completed;
        }
        if (!containers.TryGetValue(command.SourceContainerId, out ContainerRecord source))
            return Reject(command.OperationId, ContainerOperationKind.Withdraw,
                ContainerOperationStatus.NotFound, "source-not-found");
        if (!Authorized(command.ActorIdentity, source))
            return Reject(command.OperationId, ContainerOperationKind.Withdraw,
                ContainerOperationStatus.Unauthorized, "container-private");
        if (source.Revision != command.ExpectedSourceRevision)
            return Reject(command.OperationId, ContainerOperationKind.Withdraw,
                ContainerOperationStatus.StaleRevision, "source-revision-stale");
        if (source.Revision == uint.MaxValue)
            return Reject(command.OperationId, ContainerOperationKind.Withdraw,
                ContainerOperationStatus.InvalidState, "container-revision-exhausted");
        StoredItemRecord item = FindDirect(source.PersistentContainerId, command.SourceSlot);
        if (item == null)
            return Reject(command.OperationId, ContainerOperationKind.Withdraw,
                ContainerOperationStatus.NotFound, "source-slot-empty");
        if (item.LifecycleState != ItemLifecycleState.Cold)
            return Reject(command.OperationId, ContainerOperationKind.Withdraw,
                ContainerOperationStatus.OperationConflict, "item-not-cold");
        item.LifecycleState = ItemLifecycleState.PreparingWithdrawal;
        preparingWithdrawals[command.OperationId] = item.PersistentItemId;
        stagedRecord = item.Clone();
        return Accept(command.OperationId, ContainerOperationKind.Withdraw,
            sourceRevision: source.Revision, itemId: item.PersistentItemId,
            state: item.LifecycleState);
    }

    public ContainerOperationResult MarkWithdrawalMaterialized(Guid operationId)
    {
        if (!TryPreparing(operationId, out StoredItemRecord item))
            return Reject(operationId, ContainerOperationKind.Withdraw,
                ContainerOperationStatus.NotFound, "withdrawal-not-prepared");
        if (item.LifecycleState != ItemLifecycleState.PreparingWithdrawal)
            return Reject(operationId, ContainerOperationKind.Withdraw,
                ContainerOperationStatus.InvalidState, "withdrawal-state-invalid");
        item.LifecycleState = ItemLifecycleState.MaterializedPendingCommit;
        return Accept(operationId, ContainerOperationKind.Withdraw,
            itemId: item.PersistentItemId, state: item.LifecycleState);
    }

    public ContainerOperationResult CommitWithdrawal(Guid operationId)
    {
        if (TryCompleted(operationId, out ContainerOperationResult completed))
            return completed;
        if (!TryPreparing(operationId, out StoredItemRecord item))
            return Reject(operationId, ContainerOperationKind.Withdraw,
                ContainerOperationStatus.NotFound, "withdrawal-not-prepared");
        if (item.LifecycleState != ItemLifecycleState.MaterializedPendingCommit)
            return Reject(operationId, ContainerOperationKind.Withdraw,
                ContainerOperationStatus.InvalidState, "withdrawal-not-materialized");
        ContainerRecord source = containers[item.ParentContainerId];
        source.Revision = NextRevision(source.Revision);
        item.LifecycleState = ItemLifecycleState.Materialized;
        item.ParentContainerId = Guid.Empty;
        item.Slot = -1;
        preparingWithdrawals.Remove(operationId);
        ContainerOperationResult result = Accept(operationId, ContainerOperationKind.Withdraw,
            sourceRevision: source.Revision, itemId: item.PersistentItemId,
            state: item.LifecycleState);
        items.Remove(item.PersistentItemId);
        return Complete(operationId, result);
    }

    public ContainerOperationResult AbortWithdrawal(Guid operationId, string reason)
    {
        if (!TryPreparing(operationId, out StoredItemRecord item))
            return Reject(operationId, ContainerOperationKind.Withdraw,
                ContainerOperationStatus.NotFound, "withdrawal-not-prepared");
        item.LifecycleState = ItemLifecycleState.Cold;
        preparingWithdrawals.Remove(operationId);
        return Reject(operationId, ContainerOperationKind.Withdraw,
            ContainerOperationStatus.InvalidState, reason ?? "withdrawal-aborted");
    }

    /// <summary>
    /// Whether the item, and everything inside it when it's a box, is settled: no deposit or
    /// withdrawal of any of it is part way (OI-LIST-18). A live shell's id checks its contents.
    /// </summary>
    public bool IsSettled(Guid id)
    {
        if (items.TryGetValue(id, out StoredItemRecord own) && own.LifecycleState != ItemLifecycleState.Cold)
            return false;
        HashSet<Guid> seen = new() { id };
        Queue<Guid> open = new();
        open.Enqueue(id);
        while (open.Count > 0)
        {
            Guid container = open.Dequeue();
            foreach (StoredItemRecord inside in items.Values.Where(item => item.ParentContainerId == container))
            {
                if (inside.LifecycleState != ItemLifecycleState.Cold)
                    return false;
                if (seen.Add(inside.PersistentItemId))
                    open.Enqueue(inside.PersistentItemId);
            }
        }
        return true;
    }

    /// <summary>
    /// The server takes a settled row out of its box with no player's request (a retrieval:
    /// docs/design/items/containers.md, "Taken out by the server"): it is stored no longer, and its
    /// box's revision moves on. A box's own container and contents stay, for its shell to bind by
    /// its id. Null, with the reason, when it isn't stored or isn't settled.
    /// </summary>
    public StoredItemRecord TakeOut(Guid itemId, out string reason)
    {
        reason = string.Empty;
        if (!items.TryGetValue(itemId, out StoredItemRecord item))
        {
            reason = "item-not-found";
            return null;
        }
        if (!IsSettled(itemId))
        {
            reason = "container-item-busy";
            return null;
        }
        items.Remove(itemId);
        if (containers.TryGetValue(item.ParentContainerId, out ContainerRecord parent))
            parent.Revision = NextRevision(parent.Revision);
        return item.Clone();
    }

    /// <summary>
    /// The server puts a row in a container's slot with no player's request: ammo going with its tool
    /// as the tool becomes a row (containers.md, "Ammo in a magazine"). Checked as a load checks a
    /// row (the slot, the size, the depth); settled at once, as a stored item is. False, with the
    /// reason, if it won't go.
    /// </summary>
    public bool Stow(StoredItemRecord row, out string reason)
    {
        reason = ImportItem(row);
        if (reason.Length > 0)
            return false;
        ContainerRecord parent = containers[row.ParentContainerId];
        parent.Revision = NextRevision(parent.Revision);
        return true;
    }

    /// <summary>
    /// A box's shell is gone for good (OI-CON-11): its container goes, and its rows come out of the
    /// graph, returned (with their box and slot) for the server to deal with. A box among them keeps
    /// its own container and contents until the server decides. Empty when there's no such container.
    /// </summary>
    public IReadOnlyList<StoredItemRecord> RemoveContainer(Guid containerId)
    {
        if (!containers.Remove(containerId))
            return Array.Empty<StoredItemRecord>();
        List<StoredItemRecord> direct = items.Values.Where(item => item.ParentContainerId == containerId)
            .Select(item => item.Clone()).ToList();
        foreach (StoredItemRecord row in direct)
        {
            items.Remove(row.PersistentItemId);
            foreach (Guid operation in preparingWithdrawals.Where(pair => pair.Value == row.PersistentItemId)
                         .Select(pair => pair.Key).ToArray())
                preparingWithdrawals.Remove(operation);
        }
        return direct;
    }

    /// <summary>
    /// Whether the move would be committed, changing nothing, and which item it moves
    /// (<see cref="ContainerOperationResult.ItemId"/>): the server moves the ledger between the
    /// check and the commit, as for a deposit.
    /// </summary>
    public ContainerOperationResult CheckMove(MoveCommand command)
    {
        if (command == null)
            return Reject(Guid.Empty, ContainerOperationKind.Move,
                ContainerOperationStatus.InvalidRecord, "move-command-null");
        if (TryCompleted(command.OperationId, out ContainerOperationResult completed))
            return completed;
        if (!containers.TryGetValue(command.SourceContainerId, out ContainerRecord source) ||
            !containers.TryGetValue(command.DestinationContainerId, out ContainerRecord destination))
            return Reject(command.OperationId, ContainerOperationKind.Move,
                ContainerOperationStatus.NotFound, "source-or-destination-not-found");
        if (!Authorized(command.ActorIdentity, source) || !Authorized(command.ActorIdentity, destination))
            return Reject(command.OperationId, ContainerOperationKind.Move,
                ContainerOperationStatus.Unauthorized, "container-private");
        if (source.Revision != command.ExpectedSourceRevision ||
            destination.Revision != command.ExpectedDestinationRevision)
            return Reject(command.OperationId, ContainerOperationKind.Move,
                ContainerOperationStatus.StaleRevision, "container-revision-stale");
        if (source.Revision == uint.MaxValue)
            return Reject(command.OperationId, ContainerOperationKind.Move,
                ContainerOperationStatus.InvalidState, "container-revision-exhausted");
        StoredItemRecord item = FindDirect(source.PersistentContainerId, command.SourceSlot);
        if (item == null)
            return Reject(command.OperationId, ContainerOperationKind.Move,
                ContainerOperationStatus.NotFound, "source-slot-empty");
        ContainerOperationResult validation = ValidateDestination(command.OperationId,
            ContainerOperationKind.Move, command.ActorIdentity, destination,
            command.ExpectedDestinationRevision, command.DestinationSlot, item,
            ContainerOf(item), ignoreItemId: item.PersistentItemId);
        if (!validation.Accepted)
            return validation;
        return Accept(command.OperationId, ContainerOperationKind.Move, source.Revision,
            destination.Revision, item.PersistentItemId, item.LifecycleState);
    }

    public ContainerOperationResult Move(MoveCommand command)
    {
        if (command != null && TryCompleted(command.OperationId, out ContainerOperationResult completed))
            return completed;
        ContainerOperationResult check = CheckMove(command);
        if (!check.Accepted)
            return command == null ? check : Complete(command.OperationId, check);
        ContainerRecord source = containers[command.SourceContainerId];
        ContainerRecord destination = containers[command.DestinationContainerId];
        StoredItemRecord item = items[check.ItemId];
        item.ParentContainerId = destination.PersistentContainerId;
        item.Slot = command.DestinationSlot;
        source.Revision = NextRevision(source.Revision);
        if (source.PersistentContainerId == destination.PersistentContainerId)
            destination.Revision = source.Revision;
        else
            destination.Revision = NextRevision(destination.Revision);
        return Complete(command.OperationId, Accept(command.OperationId,
            ContainerOperationKind.Move, source.Revision, destination.Revision,
            item.PersistentItemId, item.LifecycleState));
    }

    public ContainerGraphSnapshot ExportSnapshot() => new()
    {
        Version = 3,
        Containers = containers.Values.Select(value => value.Clone()).ToList(),
        Items = items.Values.Where(value =>
            value.LifecycleState != ItemLifecycleState.Materialized).Select(value =>
            {
                StoredItemRecord clone = value.Clone();
                // Runtime cleanup/reservation phases are not durable authority. After a process
                // restart no Unity representation survives, so every logically committed item is cold.
                clone.LifecycleState = ItemLifecycleState.Cold;
                clone.RetiringNetId = 0;
                return clone;
            }).ToList()
    };

    /// <summary>
    /// Loads rows from a save, each on its own (docs/design/items/save-load.md, "Rows stand alone";
    /// C1): the containers, then the stored items. A row that breaks a rule (a slot out of range or
    /// taken, a container it isn't in, a container inside itself, nesting deeper than the limit)
    /// is refused and returned, and every other row loads. Owners are the ledger's, so a row has
    /// none to check. The caller keeps the refused rows as they were saved. Replaces whatever the
    /// graph held.
    /// </summary>
    public IReadOnlyList<ContainerRowRefusal> ImportRows(IEnumerable<ContainerRecord> containerRows,
        IEnumerable<StoredItemRecord> itemRows)
    {
        containers.Clear();
        items.Clear();
        preparingWithdrawals.Clear();
        List<ContainerRowRefusal> refused = new();
        foreach (ContainerRecord row in containerRows ?? Enumerable.Empty<ContainerRecord>())
        {
            ContainerOperationResult added = AddContainer(row);
            if (!added.Accepted)
                refused.Add(new ContainerRowRefusal(row, null, added.Reason));
        }
        foreach (StoredItemRecord row in itemRows ?? Enumerable.Empty<StoredItemRecord>())
        {
            string reason = ImportItem(row);
            if (reason.Length > 0)
                refused.Add(new ContainerRowRefusal(null, row, reason));
        }
        return refused;
    }

    private string ImportItem(StoredItemRecord input)
    {
        if (input == null || input.PersistentItemId == Guid.Empty)
            return "item-id-missing";
        if (items.ContainsKey(input.PersistentItemId))
            return "duplicate-item-id";
        if (!containers.TryGetValue(input.ParentContainerId, out ContainerRecord parent))
            return "container-not-found";
        if ((input.DetachedState?.Length ?? 0) > Limits.MaximumDetachedStateBytes)
            return "detached-state-too-large";
        if (input.Slot < 0 || input.Slot >= parent.Capacity)
            return "container-slot-invalid";
        if (FindDirect(input.ParentContainerId, input.Slot) != null)
            return "container-slot-occupied";
        StoredItemRecord clone = input.Clone();
        clone.SessionHandle = AllocateHandle();
        // After a restart no Unity representation survives: every stored item is cold.
        clone.LifecycleState = ItemLifecycleState.Cold;
        clone.RetiringNetId = 0;
        items.Add(clone.PersistentItemId, clone);
        ContainerOperationResult depth = ValidateDepthFrom(RootOf(parent.PersistentContainerId));
        if (depth.Accepted)
            return string.Empty;
        items.Remove(clone.PersistentItemId);
        return depth.Reason;
    }

    /// <summary>Whether this item is a container's shell: the container has the item's id.</summary>
    public bool IsContainer(Guid itemId) => containers.ContainsKey(itemId);

    /// <summary>A container's own rows (not those of boxes inside it), as copies, by slot.</summary>
    public IReadOnlyList<StoredItemRecord> RowsIn(Guid containerId) => items.Values
        .Where(item => item.ParentContainerId == containerId).OrderBy(item => item.Slot)
        .Select(item => item.Clone()).ToList();

    // The outermost container above this one: a container is stored as the item with its id.
    private Guid RootOf(Guid containerId)
    {
        HashSet<Guid> seen = new();
        Guid current = containerId;
        while (seen.Add(current) && items.TryGetValue(current, out StoredItemRecord holder))
            current = holder.ParentContainerId;
        return current;
    }

    // How many boxes this one is inside: 0 for the outermost.
    private int DepthOf(Guid containerId)
    {
        HashSet<Guid> seen = new();
        Guid current = containerId;
        int depth = 0;
        while (seen.Add(current) && items.TryGetValue(current, out StoredItemRecord holder))
        {
            current = holder.ParentContainerId;
            depth++;
        }
        return depth;
    }

    // How many boxes deep this one's contents go: 0 when it holds none.
    private int HeightOf(Guid containerId)
    {
        HashSet<Guid> visited = new();
        Queue<(Guid id, int depth)> queue = new();
        queue.Enqueue((containerId, 0));
        int height = 0;
        while (queue.Count > 0 && visited.Count <= Limits.MaximumVisitedNodes)
        {
            (Guid current, int depth) = queue.Dequeue();
            if (!visited.Add(current))
                continue;
            height = Math.Max(height, depth);
            foreach (Guid child in DirectChildContainers(current))
                queue.Enqueue((child, depth + 1));
        }
        return height;
    }

    // The container an item opens, if it is one's shell.
    private Guid? ContainerOf(StoredItemRecord item) =>
        item != null && containers.ContainsKey(item.PersistentItemId) ? item.PersistentItemId : null;

    public ContainerOperationResult ValidateWholeGraph()
    {
        foreach (ContainerRecord container in containers.Values)
        {
            List<StoredItemRecord> direct = items.Values.Where(item =>
                item.ParentContainerId == container.PersistentContainerId &&
                item.LifecycleState != ItemLifecycleState.Materialized).ToList();
            if (direct.Any(item => item.Slot < 0 || item.Slot >= container.Capacity) ||
                direct.GroupBy(item => item.Slot).Any(group => group.Count() > 1))
                return Reject(Guid.Empty, ContainerOperationKind.Browse,
                    ContainerOperationStatus.InvalidRecord, "container-slot-invalid");
            ContainerOperationResult traversal = ValidateDepthFrom(container.PersistentContainerId);
            if (!traversal.Accepted) return traversal;
        }
        foreach (IGrouping<string, StoredItemRecord> owner in items.Values
                     .GroupBy(item => OwnerOf(item.PersistentItemId), StringComparer.Ordinal)
                     .Where(group => group.Key.Length > 0))
            if (owner.Count() > Limits.MaximumItemsPerOwner)
                return Reject(Guid.Empty, ContainerOperationKind.Browse,
                    ContainerOperationStatus.OwnerQuotaExceeded, "owner-item-quota-exceeded");
        return Accept(Guid.Empty, ContainerOperationKind.Browse);
    }
    private ContainerOperationResult ValidateDestination(Guid operationId,
        ContainerOperationKind kind, string actorIdentity, ContainerRecord destination,
        uint expectedRevision, int slot, StoredItemRecord item, Guid? childContainerId,
        Guid? ignoreItemId = null)
    {
        if (!Authorized(actorIdentity, destination))
            return Reject(operationId, kind, ContainerOperationStatus.Unauthorized,
                "container-private");
        if (destination.Revision != expectedRevision)
            return Reject(operationId, kind, ContainerOperationStatus.StaleRevision,
                "destination-revision-stale");
        if (destination.Revision == uint.MaxValue)
            return Reject(operationId, kind, ContainerOperationStatus.InvalidState,
                "container-revision-exhausted");
        if (slot < 0 || slot >= destination.Capacity)
            return Reject(operationId, kind, ContainerOperationStatus.SlotOutOfRange,
                "destination-slot-invalid");
        if (FindDirect(destination.PersistentContainerId, slot, ignoreItemId) != null)
            return Reject(operationId, kind, ContainerOperationStatus.SlotOccupied,
                "destination-slot-occupied");
        if (item == null || !compatibility.IsCompatible(item, destination))
            return Reject(operationId, kind, ContainerOperationStatus.Incompatible,
                "destination-incompatible");
        if (childContainerId.HasValue)
        {
            if (!containers.ContainsKey(childContainerId.Value))
                return Reject(operationId, kind, ContainerOperationStatus.NotFound,
                    "child-container-not-found");
            ContainerOperationResult cycle = ValidateNoCycle(childContainerId.Value,
                destination.PersistentContainerId);
            if (!cycle.Accepted) return cycle;
            // The same limit the load holds a save to (OI-CON-15): a box nested deeper than
            // MaximumDepth inside the outermost one is refused here, so no save is made that
            // the load would refuse.
            if (DepthOf(destination.PersistentContainerId) + 1 + HeightOf(childContainerId.Value) >
                Limits.MaximumDepth)
                return Reject(operationId, kind, ContainerOperationStatus.DepthExceeded,
                    "container-depth-exceeded");
        }
        return Accept(operationId, kind, destinationRevision: destination.Revision);
    }

    private ContainerOperationResult ValidateNoCycle(Guid movingContainerId, Guid destinationId)
    {
        if (movingContainerId == destinationId)
            return Reject(Guid.Empty, ContainerOperationKind.Move,
                ContainerOperationStatus.CycleDetected, "container-self-cycle");
        HashSet<Guid> visited = new();
        Queue<(Guid id, int depth)> queue = new();
        queue.Enqueue((movingContainerId, 0));
        while (queue.Count > 0)
        {
            (Guid current, int depth) = queue.Dequeue();
            if (!visited.Add(current))
                return Reject(Guid.Empty, ContainerOperationKind.Move,
                    ContainerOperationStatus.CycleDetected, "container-cycle-detected");
            if (visited.Count > Limits.MaximumVisitedNodes)
                return Reject(Guid.Empty, ContainerOperationKind.Move,
                    ContainerOperationStatus.NodeBudgetExceeded, "container-node-budget-exceeded");
            if (depth > Limits.MaximumDepth)
                return Reject(Guid.Empty, ContainerOperationKind.Move,
                    ContainerOperationStatus.DepthExceeded, "container-depth-exceeded");
            if (current == destinationId)
                return Reject(Guid.Empty, ContainerOperationKind.Move,
                    ContainerOperationStatus.CycleDetected, "destination-is-descendant");
            foreach (Guid child in DirectChildContainers(current))
                queue.Enqueue((child, depth + 1));
        }
        return Accept(Guid.Empty, ContainerOperationKind.Move);
    }

    private ContainerOperationResult ValidateDepthFrom(Guid root)
    {
        HashSet<Guid> visited = new();
        Queue<(Guid id, int depth)> queue = new();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            (Guid current, int depth) = queue.Dequeue();
            if (!visited.Add(current))
                return Reject(Guid.Empty, ContainerOperationKind.Browse,
                    ContainerOperationStatus.CycleDetected, "container-cycle-detected");
            if (visited.Count > Limits.MaximumVisitedNodes)
                return Reject(Guid.Empty, ContainerOperationKind.Browse,
                    ContainerOperationStatus.NodeBudgetExceeded, "container-node-budget-exceeded");
            if (depth > Limits.MaximumDepth)
                return Reject(Guid.Empty, ContainerOperationKind.Browse,
                    ContainerOperationStatus.DepthExceeded, "container-depth-exceeded");
            foreach (Guid child in DirectChildContainers(current))
                queue.Enqueue((child, depth + 1));
        }
        return Accept(Guid.Empty, ContainerOperationKind.Browse);
    }

    private IEnumerable<Guid> DirectChildContainers(Guid parent) => items.Values
        .Where(item => item.ParentContainerId == parent && containers.ContainsKey(item.PersistentItemId) &&
            item.LifecycleState != ItemLifecycleState.Materialized)
        .Select(item => item.PersistentItemId);

    private StoredItemRecord FindDirect(Guid containerId, int slot, Guid? ignore = null) =>
        items.Values.FirstOrDefault(item => item.ParentContainerId == containerId &&
            item.Slot == slot && item.LifecycleState != ItemLifecycleState.Materialized &&
            (!ignore.HasValue || item.PersistentItemId != ignore.Value));

    private bool TryPreparing(Guid operationId, out StoredItemRecord item)
    {
        item = null;
        return preparingWithdrawals.TryGetValue(operationId, out Guid itemId) &&
            items.TryGetValue(itemId, out item);
    }

    private bool TryCompleted(Guid operationId, out ContainerOperationResult result)
    {
        if (operationId != Guid.Empty && completedOperations.TryGetValue(operationId, out result))
        {
            result = result.Clone();
            return true;
        }
        result = null;
        return false;
    }

    private ContainerOperationResult Complete(Guid operationId, ContainerOperationResult result)
    {
        if (operationId != Guid.Empty)
        {
            if (!completedOperations.ContainsKey(operationId))
                completedOperationOrder.Enqueue(operationId);
            completedOperations[operationId] = result.Clone();
            while (completedOperationOrder.Count > 4096)
                completedOperations.Remove(completedOperationOrder.Dequeue());
        }
        return result;
    }

    /// <summary>The item's owner, from the ledger: empty when nobody owns it.</summary>
    public string OwnerOf(Guid itemId) => owners.OwnerOf(itemId) ?? string.Empty;

    /// <summary>
    /// Who may open the container (OI-CON-01, OI-CON-08): its shell's owner; one nobody owns
    /// stored in another is kept by whoever keeps that one; a live one by whoever holds it. Empty
    /// when nobody may.
    /// </summary>
    public string KeeperOf(Guid containerId)
    {
        HashSet<Guid> seen = new();
        Guid current = containerId;
        while (seen.Add(current))
        {
            string owner = OwnerOf(current);
            if (owner.Length > 0)
                return owner;
            if (!items.TryGetValue(current, out StoredItemRecord stored))
                return owners.HolderOf(current) ?? string.Empty;
            current = stored.ParentContainerId;
        }
        return string.Empty;
    }

    private int CountOwnedItems(string owner) => items.Values.Count(item =>
        item.LifecycleState != ItemLifecycleState.Materialized &&
        string.Equals(OwnerOf(item.PersistentItemId), owner, StringComparison.Ordinal));

    private int CountKeptItems(string keeper)
    {
        if (keeper.Length == 0)
            return 0;
        HashSet<Guid> kept = new(containers.Keys.Where(id =>
            string.Equals(KeeperOf(id), keeper, StringComparison.Ordinal)));
        return items.Values.Count(item => item.LifecycleState != ItemLifecycleState.Materialized &&
            kept.Contains(item.ParentContainerId));
    }

    private bool Authorized(string actor, ContainerRecord container) =>
        !string.IsNullOrWhiteSpace(actor) &&
        string.Equals(actor, KeeperOf(container.PersistentContainerId), StringComparison.Ordinal);

    private uint AllocateHandle()
    {
        uint value = nextSessionHandle++;
        if (value == 0) value = nextSessionHandle++;
        return value;
    }

    private static uint NextRevision(uint revision) => revision == uint.MaxValue
        ? uint.MaxValue : revision + 1;

    private static ContainerOperationResult Accept(Guid operationId, ContainerOperationKind kind,
        uint sourceRevision = 0, uint destinationRevision = 0, Guid itemId = default,
        ItemLifecycleState state = ItemLifecycleState.Cold) => new()
    {
        OperationId = operationId,
        Kind = kind,
        Status = ContainerOperationStatus.Accepted,
        SourceRevision = sourceRevision,
        DestinationRevision = destinationRevision,
        ItemId = itemId,
        ItemState = state
    };

    private static ContainerOperationResult Reject(Guid operationId, ContainerOperationKind kind,
        ContainerOperationStatus status, string reason) => new()
    {
        OperationId = operationId,
        Kind = kind,
        Status = status,
        Reason = reason ?? string.Empty
    };
}
