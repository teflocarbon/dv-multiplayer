using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Essentials;

namespace Multiplayer.Core.Items.Ledger;

/// <summary>What the server asks the ledger to record when an item becomes owned.</summary>
public sealed class BirthRequest
{
    /// <summary>The item's persistent id. Empty: the ledger mints one.</summary>
    public Guid Id;
    public Guid Owner;
    public string Prefab;
    public Home Where;
    public BirthReason Reason;
    public Guid By;
    public Guid From;
    public string Detail;
    /// <summary>The item is an essential: it gets a retrieval claim slot of its own.</summary>
    public bool Essential;
    /// <summary>The slot the essential should claim if it is free, or -1.</summary>
    public int PreferredClaimSlot = -1;
    public bool ShopStocked;
}

public enum RegistrationStatus : byte
{
    Registered,
    /// <summary>
    /// Another item already had the requested id: the request was registered under a new id, born
    /// <see cref="BirthReason.Duplicate"/>. The caller gives the item the new id.
    /// </summary>
    Reminted,
    Invalid
}

public readonly struct Registration
{
    public Registration(RegistrationStatus status, LedgerEntry entry, Guid copiedId)
    {
        Status = status;
        Entry = entry;
        CopiedId = copiedId;
    }

    public RegistrationStatus Status { get; }
    /// <summary>A copy of the entry as recorded, or null when invalid.</summary>
    public LedgerEntry Entry { get; }
    /// <summary>For <see cref="RegistrationStatus.Reminted"/>: the id that was already taken.</summary>
    public Guid CopiedId { get; }

    public bool Accepted => Status != RegistrationStatus.Invalid;
}

public enum LedgerChangeKind : byte
{
    Born,
    Moved,
    Ended,
    ShopStocked,
    ClaimSlot
}

/// <summary>One change to the ledger, raised as it happens. Entries are copies.</summary>
public readonly struct LedgerChange
{
    public LedgerChange(LedgerChangeKind kind, LedgerEntry entry, Home previousWhere)
    {
        Kind = kind;
        Entry = entry;
        PreviousWhere = previousWhere;
    }

    public LedgerChangeKind Kind { get; }
    public LedgerEntry Entry { get; }
    public Home PreviousWhere { get; }
}

public readonly struct ClaimMove
{
    public ClaimMove(Guid id, int fromSlot, int toSlot)
    {
        Id = id;
        FromSlot = fromSlot;
        ToSlot = toSlot;
    }

    public Guid Id { get; }
    public int FromSlot { get; }
    public int ToSlot { get; }
}

/// <summary>
/// The server's record of every item a player owns, keyed by the item's lifelong persistent id:
/// whose it is, where it is, and how its life began and ended. Owners and holders are identities,
/// never per-session player numbers, so an offline owner's items are as owned as anyone's.
/// See docs/design/owned-items.md.
/// </summary>
public sealed class ItemLedger
{
    /// <summary>Ended entries kept per owner for lineage ("where did my radio go"); older ones are pruned.</summary>
    public const int EndedKeptPerOwner = 100;

    private readonly Dictionary<Guid, LedgerEntry> entries = new();
    private readonly HashSet<Guid> starterKitsGiven = new();
    private readonly Func<Guid> newId;
    private readonly Func<long> utcNow;
    private readonly int claimCapacity;

    public ItemLedger(Func<Guid> newId = null, Func<long> utcNow = null,
        int claimCapacity = ClaimSlotPolicy.InventoryCapacity)
    {
        this.newId = newId ?? Guid.NewGuid;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow.Ticks);
        this.claimCapacity = claimCapacity;
    }

    public event Action<LedgerChange> Changed;

    public int Count => entries.Count;

    /// <summary>
    /// Records an item becoming owned. An id is for life, so a request naming an id the ledger already
    /// has, live or ended, is a copy (a copied save, a dupe, an object cloned with its identity): the
    /// first keeps the id and the copy is recorded under a new one, born <see cref="BirthReason.Duplicate"/>.
    /// </summary>
    public Registration Register(BirthRequest request)
    {
        if (request == null || request.Owner == Guid.Empty || string.IsNullOrEmpty(request.Prefab))
            return new Registration(RegistrationStatus.Invalid, null, Guid.Empty);

        Guid id = request.Id;
        Guid copied = Guid.Empty;
        BirthReason reason = request.Reason;
        Guid from = request.From;
        string detail = request.Detail;
        if (id != Guid.Empty && entries.TryGetValue(id, out LedgerEntry original))
        {
            copied = id;
            id = Mint();
            reason = BirthReason.Duplicate;
            from = copied;
            detail = $"copy of {original}, registered as {request.Reason}" +
                (string.IsNullOrEmpty(request.Detail) ? "" : ": " + request.Detail);
        }
        else if (id == Guid.Empty)
        {
            id = Mint();
        }

        int claimSlot = request.Essential ? FreeClaimSlot(request.Owner, request.PreferredClaimSlot) : -1;
        LedgerEntry entry = new(id, request.Owner, request.Prefab, request.Where,
            new Birth(reason, utcNow(), request.By, from, detail), claimSlot, request.ShopStocked);
        entries.Add(id, entry);
        Raise(LedgerChangeKind.Born, entry, request.Where);
        return new Registration(copied == Guid.Empty
            ? RegistrationStatus.Registered
            : RegistrationStatus.Reminted, entry.Clone(), copied);
    }

    /// <summary>
    /// Ends <paramref name="usedId"/> as vanilla uses it up and records the item it made in its place,
    /// owned by the used item's owner (spent duct tape and its empty roll).
    /// </summary>
    public Registration Replace(Guid usedId, ItemRemovalReason endReason, BirthRequest replacement)
    {
        if (replacement == null || !entries.TryGetValue(usedId, out LedgerEntry used) || !used.IsLive)
            return new Registration(RegistrationStatus.Invalid, null, Guid.Empty);
        replacement.Owner = used.Owner;
        replacement.Reason = BirthReason.Replaced;
        replacement.From = usedId;
        Registration made = Register(replacement);
        if (made.Accepted)
            End(usedId, endReason, made.Entry.Id);
        return made;
    }

    /// <summary>Records where a live item is now. False for an unknown or ended item.</summary>
    public bool Move(Guid id, Home where)
    {
        if (!entries.TryGetValue(id, out LedgerEntry entry) || !entry.IsLive)
            return false;
        if (entry.Where == where)
            return true;
        Home previous = entry.Where;
        entry.Where = where;
        Raise(LedgerChangeKind.Moved, entry, previous);
        return true;
    }

    /// <summary>Ends a live item's life. Its entry stays, for lineage, until pruned.</summary>
    public bool End(Guid id, ItemRemovalReason reason, Guid replacedBy = default, string detail = null)
    {
        if (!entries.TryGetValue(id, out LedgerEntry entry) || !entry.IsLive)
            return false;
        entry.Ended = new End(reason, utcNow(), replacedBy, detail);
        Raise(LedgerChangeKind.Ended, entry, entry.Where);
        PruneEnded(entry.Owner);
        return true;
    }

    /// <summary>
    /// Brings an ended entry back, for a load that finds its item in a store after all (a build
    /// without the ledger kept playing the save). Raised as <see cref="LedgerChangeKind.Born"/>,
    /// so anything counting live items counts it again.
    /// </summary>
    public bool Revive(Guid id, Home where)
    {
        if (!entries.TryGetValue(id, out LedgerEntry entry) || entry.IsLive)
            return false;
        Home previous = entry.Where;
        entry.Ended = null;
        entry.Where = where;
        Raise(LedgerChangeKind.Born, entry, previous);
        return true;
    }

    public bool SetShopStocked(Guid id, bool shopStocked)
    {
        if (!entries.TryGetValue(id, out LedgerEntry entry) || !entry.IsLive)
            return false;
        if (entry.ShopStocked == shopStocked)
            return true;
        entry.ShopStocked = shopStocked;
        Raise(LedgerChangeKind.ShopStocked, entry, entry.Where);
        return true;
    }

    /// <summary>
    /// Gives a live essential a claim slot, as when its owner puts it in that slot. A slot holds one
    /// item, so another of the owner's essentials claiming it moves to the highest slot no claim of
    /// theirs holds (DV fills the backpack from the front). Returns every claim that moved.
    /// </summary>
    public IReadOnlyList<ClaimMove> SetClaimSlot(Guid id, int slot)
    {
        if (slot < 0 || slot >= claimCapacity || !entries.TryGetValue(id, out LedgerEntry entry) ||
            !entry.IsLive || entry.ClaimSlot < 0)
            return Array.Empty<ClaimMove>();
        List<ClaimMove> moves = new();
        if (entry.ClaimSlot != slot)
        {
            moves.Add(new ClaimMove(id, entry.ClaimSlot, slot));
            entry.ClaimSlot = slot;
            Raise(LedgerChangeKind.ClaimSlot, entry, entry.Where);
        }
        foreach (LedgerEntry other in LiveClaims(entry.Owner).Where(e => e.Id != id && e.ClaimSlot == slot).ToArray())
            moves.Add(MoveClaim(other, FreeClaimSlot(entry.Owner, -1)));
        return moves.Where(move => move.FromSlot != move.ToSlot).ToArray();
    }

    /// <summary>
    /// Leaves each of an owner's essentials on a claim slot of its own, for a save from before the
    /// ledger. Of the claims sharing a slot, the item really in that slot keeps it, then the oldest.
    /// With every slot claimed the clash stays rather than drop a claim.
    /// </summary>
    public IReadOnlyList<ClaimMove> ResolveClaimClashes(Guid owner)
    {
        List<ClaimMove> moves = new();
        foreach (IGrouping<int, LedgerEntry> slot in LiveClaims(owner).GroupBy(e => e.ClaimSlot).ToArray())
        {
            LedgerEntry[] displaced = slot
                .OrderByDescending(e => e.Where == Home.With(owner, HoldKind.Inventory, slot.Key))
                .ThenBy(e => e.Born.UtcTicks)
                .ThenBy(e => e.Id)
                .Skip(1)
                .ToArray();
            foreach (LedgerEntry entry in displaced)
            {
                int free = FreeClaimSlot(owner, -1);
                if (free < 0)
                    return moves;
                moves.Add(MoveClaim(entry, free));
            }
        }
        return moves;
    }

    public bool TryGet(Guid id, out LedgerEntry entry)
    {
        if (id != Guid.Empty && entries.TryGetValue(id, out LedgerEntry stored))
        {
            entry = stored.Clone();
            return true;
        }
        entry = null;
        return false;
    }

    public bool IsLive(Guid id) => entries.TryGetValue(id, out LedgerEntry entry) && entry.IsLive;

    /// <summary>The owner of a live item, or empty: an unowned item has no entry.</summary>
    public Guid OwnerOf(Guid id) => entries.TryGetValue(id, out LedgerEntry entry) && entry.IsLive ? entry.Owner : Guid.Empty;

    public IReadOnlyList<LedgerEntry> LiveOwnedBy(Guid owner) => Ordered(entries.Values
        .Where(entry => entry.IsLive && entry.Owner == owner));

    /// <summary>Everything a player has with them, theirs or not: what they get back when they join.</summary>
    public IReadOnlyList<LedgerEntry> LiveWith(Guid holder) => Ordered(entries.Values
        .Where(entry => entry.IsLive && entry.Where.IsWithPlayer && entry.Where.Holder == holder));

    /// <summary>
    /// How many live items of a prefab an owner has: for issuing essentials (any), and for shop stock
    /// (<paramref name="shopStocked"/> true counts only items still carrying vanilla's Restock flag).
    /// </summary>
    public int CountLive(Guid owner, string prefab, bool? shopStocked = null) => entries.Values.Count(entry =>
        entry.IsLive && entry.Owner == owner &&
        string.Equals(entry.Prefab, prefab, StringComparison.Ordinal) &&
        (shopStocked == null || entry.ShopStocked == shopStocked.Value));

    /// <summary>Every entry, live and ended, in a stable order: what the save writes.</summary>
    public IReadOnlyList<LedgerEntry> All() => Ordered(entries.Values);

    public bool HasStarterKit(Guid player) => starterKitsGiven.Contains(player);

    public void MarkStarterKit(Guid player)
    {
        if (player != Guid.Empty)
            starterKitsGiven.Add(player);
    }

    /// <summary>Forgets that a player had their starter kit: a re-keyed identity. Returns whether they had.</summary>
    public bool ForgetStarterKit(Guid player) => starterKitsGiven.Remove(player);

    public IReadOnlyCollection<Guid> StarterKitsGiven => starterKitsGiven;

    /// <summary>Whether anything in the ledger is a player's: an entry they own, have with them or
    /// caused, or their starter kit.</summary>
    public bool HasIdentity(Guid player) =>
        player != Guid.Empty && (starterKitsGiven.Contains(player) || entries.Values.Any(entry =>
            entry.Owner == player || entry.Where.Holder == player || entry.Born.By == player));

    /// <summary>
    /// A player's identity changed (an install GUID re-keyed to their Steam account): what they own,
    /// what they have with them, what they caused and their starter kit move to the new identity,
    /// ended entries included, raising nothing (nothing moved). Returns how many entries changed.
    /// </summary>
    public int Rekey(Guid from, Guid to)
    {
        if (from == Guid.Empty || to == Guid.Empty || from == to)
            return 0;
        int changed = 0;
        foreach (LedgerEntry entry in entries.Values)
        {
            bool touched = false;
            if (entry.Owner == from)
            {
                entry.Owner = to;
                touched = true;
            }
            if (entry.Where.IsWithPlayer && entry.Where.Holder == from)
            {
                entry.Where = Home.With(to, entry.Where.Hold, entry.Where.Slot);
                touched = true;
            }
            if (entry.Born.By == from)
            {
                entry.Born = new Birth(entry.Born.Reason, entry.Born.UtcTicks, to, entry.Born.From, entry.Born.Detail);
                touched = true;
            }
            if (touched)
                changed++;
        }
        if (starterKitsGiven.Remove(from))
            starterKitsGiven.Add(to);
        return changed;
    }

    /// <summary>
    /// Replaces the ledger with saved entries, raising nothing. An id saved twice keeps its first
    /// entry. Returns the entries it refused.
    /// </summary>
    public IReadOnlyList<LedgerEntry> Restore(IEnumerable<LedgerEntry> saved, IEnumerable<Guid> starterKits)
    {
        entries.Clear();
        starterKitsGiven.Clear();
        List<LedgerEntry> refused = new();
        foreach (LedgerEntry entry in saved ?? Enumerable.Empty<LedgerEntry>())
        {
            if (entry == null || entry.Id == Guid.Empty || entry.Owner == Guid.Empty || entries.ContainsKey(entry.Id))
                refused.Add(entry);
            else
                entries.Add(entry.Id, entry.Clone());
        }
        foreach (Guid player in starterKits ?? Enumerable.Empty<Guid>())
            MarkStarterKit(player);
        return refused;
    }

    private Guid Mint()
    {
        Guid id;
        do id = newId();
        while (id == Guid.Empty || entries.ContainsKey(id));
        return id;
    }

    private IEnumerable<LedgerEntry> LiveClaims(Guid owner) =>
        entries.Values.Where(entry => entry.IsLive && entry.Owner == owner && entry.ClaimSlot >= 0);

    private int FreeClaimSlot(Guid owner, int preferred)
    {
        HashSet<int> taken = new(LiveClaims(owner).Select(entry => entry.ClaimSlot));
        if (preferred >= 0 && preferred < claimCapacity && !taken.Contains(preferred))
            return preferred;
        for (int slot = claimCapacity - 1; slot >= 0; slot--)
            if (!taken.Contains(slot))
                return slot;
        return -1;
    }

    private ClaimMove MoveClaim(LedgerEntry entry, int slot)
    {
        ClaimMove move = new(entry.Id, entry.ClaimSlot, slot < 0 ? entry.ClaimSlot : slot);
        if (slot >= 0 && slot != entry.ClaimSlot)
        {
            entry.ClaimSlot = slot;
            Raise(LedgerChangeKind.ClaimSlot, entry, entry.Where);
        }
        return move;
    }

    private void PruneEnded(Guid owner)
    {
        LedgerEntry[] ended = entries.Values
            .Where(entry => !entry.IsLive && entry.Owner == owner)
            .OrderByDescending(entry => entry.Ended.UtcTicks)
            .ThenBy(entry => entry.Id)
            .ToArray();
        foreach (LedgerEntry old in ended.Skip(EndedKeptPerOwner))
            entries.Remove(old.Id);
    }

    private static IReadOnlyList<LedgerEntry> Ordered(IEnumerable<LedgerEntry> source) => source
        .OrderBy(entry => entry.Owner)
        .ThenBy(entry => entry.Born.UtcTicks)
        .ThenBy(entry => entry.Id)
        .Select(entry => entry.Clone())
        .ToArray();

    private void Raise(LedgerChangeKind kind, LedgerEntry entry, Home previous) =>
        Changed?.Invoke(new LedgerChange(kind, entry.Clone(), previous));
}
