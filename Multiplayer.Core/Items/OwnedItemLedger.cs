using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Items;

/// <summary>What the server asks the ledger to record when an item becomes owned.</summary>
public sealed class OwnedItemBirthRequest
{
    /// <summary>The item's persistent id. Empty: the ledger mints one.</summary>
    public Guid Id;
    public Guid Owner;
    public string Prefab;
    public OwnedItemWhere Where;
    public OwnedItemBirth Reason;
    public Guid By;
    public Guid From;
    public string Detail;
    /// <summary>The item is an essential: it gets a retrieval claim slot of its own.</summary>
    public bool Essential;
    /// <summary>The slot the essential should claim if it is free, or -1.</summary>
    public int PreferredClaimSlot = -1;
    public bool ShopStocked;
}

public enum OwnedItemRegistrationStatus : byte
{
    Registered,
    /// <summary>
    /// Another item already had the requested id: the request was registered under a new id, born
    /// <see cref="OwnedItemBirth.Duplicate"/>. The caller gives the item the new id.
    /// </summary>
    Reminted,
    Invalid
}

public readonly struct OwnedItemRegistration
{
    public OwnedItemRegistration(OwnedItemRegistrationStatus status, OwnedItemEntry entry, Guid copiedId)
    {
        Status = status;
        Entry = entry;
        CopiedId = copiedId;
    }

    public OwnedItemRegistrationStatus Status { get; }
    /// <summary>A copy of the entry as recorded, or null when invalid.</summary>
    public OwnedItemEntry Entry { get; }
    /// <summary>For <see cref="OwnedItemRegistrationStatus.Reminted"/>: the id that was already taken.</summary>
    public Guid CopiedId { get; }

    public bool Accepted => Status != OwnedItemRegistrationStatus.Invalid;
}

public enum OwnedItemChangeKind : byte
{
    Born,
    Moved,
    Ended,
    ShopStocked,
    ClaimSlot
}

/// <summary>One change to the ledger, raised as it happens. Entries are copies.</summary>
public readonly struct OwnedItemChange
{
    public OwnedItemChange(OwnedItemChangeKind kind, OwnedItemEntry entry, OwnedItemWhere previousWhere)
    {
        Kind = kind;
        Entry = entry;
        PreviousWhere = previousWhere;
    }

    public OwnedItemChangeKind Kind { get; }
    public OwnedItemEntry Entry { get; }
    public OwnedItemWhere PreviousWhere { get; }
}

public readonly struct OwnedItemClaimMove
{
    public OwnedItemClaimMove(Guid id, int fromSlot, int toSlot)
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
public sealed class OwnedItemLedger
{
    /// <summary>Ended entries kept per owner for lineage ("where did my radio go"); older ones are pruned.</summary>
    public const int EndedKeptPerOwner = 100;

    private readonly Dictionary<Guid, OwnedItemEntry> entries = new();
    private readonly HashSet<Guid> starterKitsGiven = new();
    private readonly Func<Guid> newId;
    private readonly Func<long> utcNow;
    private readonly int claimCapacity;

    public OwnedItemLedger(Func<Guid> newId = null, Func<long> utcNow = null,
        int claimCapacity = EssentialClaimSlotPolicy.InventoryCapacity)
    {
        this.newId = newId ?? Guid.NewGuid;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow.Ticks);
        this.claimCapacity = claimCapacity;
    }

    public event Action<OwnedItemChange> Changed;

    public int Count => entries.Count;

    /// <summary>
    /// Records an item becoming owned. An id is for life, so a request naming an id the ledger already
    /// has, live or ended, is a copy (a copied save, a dupe, an object cloned with its identity): the
    /// first keeps the id and the copy is recorded under a new one, born <see cref="OwnedItemBirth.Duplicate"/>.
    /// </summary>
    public OwnedItemRegistration Register(OwnedItemBirthRequest request)
    {
        if (request == null || request.Owner == Guid.Empty || string.IsNullOrEmpty(request.Prefab))
            return new OwnedItemRegistration(OwnedItemRegistrationStatus.Invalid, null, Guid.Empty);

        Guid id = request.Id;
        Guid copied = Guid.Empty;
        OwnedItemBirth reason = request.Reason;
        Guid from = request.From;
        string detail = request.Detail;
        if (id != Guid.Empty && entries.TryGetValue(id, out OwnedItemEntry original))
        {
            copied = id;
            id = Mint();
            reason = OwnedItemBirth.Duplicate;
            from = copied;
            detail = $"copy of {original}, registered as {request.Reason}" +
                (string.IsNullOrEmpty(request.Detail) ? "" : ": " + request.Detail);
        }
        else if (id == Guid.Empty)
        {
            id = Mint();
        }

        int claimSlot = request.Essential ? FreeClaimSlot(request.Owner, request.PreferredClaimSlot) : -1;
        OwnedItemEntry entry = new(id, request.Owner, request.Prefab, request.Where,
            new OwnedItemBorn(reason, utcNow(), request.By, from, detail), claimSlot, request.ShopStocked);
        entries.Add(id, entry);
        Raise(OwnedItemChangeKind.Born, entry, request.Where);
        return new OwnedItemRegistration(copied == Guid.Empty
            ? OwnedItemRegistrationStatus.Registered
            : OwnedItemRegistrationStatus.Reminted, entry.Clone(), copied);
    }

    /// <summary>
    /// Ends <paramref name="usedId"/> as vanilla uses it up and records the item it made in its place,
    /// owned by the used item's owner (spent duct tape and its empty roll).
    /// </summary>
    public OwnedItemRegistration Replace(Guid usedId, ItemRemovalReason endReason, OwnedItemBirthRequest replacement)
    {
        if (replacement == null || !entries.TryGetValue(usedId, out OwnedItemEntry used) || !used.IsLive)
            return new OwnedItemRegistration(OwnedItemRegistrationStatus.Invalid, null, Guid.Empty);
        replacement.Owner = used.Owner;
        replacement.Reason = OwnedItemBirth.Replaced;
        replacement.From = usedId;
        OwnedItemRegistration made = Register(replacement);
        if (made.Accepted)
            End(usedId, endReason, made.Entry.Id);
        return made;
    }

    /// <summary>Records where a live item is now. False for an unknown or ended item.</summary>
    public bool Move(Guid id, OwnedItemWhere where)
    {
        if (!entries.TryGetValue(id, out OwnedItemEntry entry) || !entry.IsLive)
            return false;
        if (entry.Where == where)
            return true;
        OwnedItemWhere previous = entry.Where;
        entry.Where = where;
        Raise(OwnedItemChangeKind.Moved, entry, previous);
        return true;
    }

    /// <summary>Ends a live item's life. Its entry stays, for lineage, until pruned.</summary>
    public bool End(Guid id, ItemRemovalReason reason, Guid replacedBy = default, string detail = null)
    {
        if (!entries.TryGetValue(id, out OwnedItemEntry entry) || !entry.IsLive)
            return false;
        entry.Ended = new OwnedItemEnded(reason, utcNow(), replacedBy, detail);
        Raise(OwnedItemChangeKind.Ended, entry, entry.Where);
        PruneEnded(entry.Owner);
        return true;
    }

    /// <summary>
    /// Brings an ended entry back, for a load that finds its item in a store after all (a build
    /// without the ledger kept playing the save). Raised as <see cref="OwnedItemChangeKind.Born"/>,
    /// so anything counting live items counts it again.
    /// </summary>
    public bool Revive(Guid id, OwnedItemWhere where)
    {
        if (!entries.TryGetValue(id, out OwnedItemEntry entry) || entry.IsLive)
            return false;
        OwnedItemWhere previous = entry.Where;
        entry.Ended = null;
        entry.Where = where;
        Raise(OwnedItemChangeKind.Born, entry, previous);
        return true;
    }

    public bool SetShopStocked(Guid id, bool shopStocked)
    {
        if (!entries.TryGetValue(id, out OwnedItemEntry entry) || !entry.IsLive)
            return false;
        if (entry.ShopStocked == shopStocked)
            return true;
        entry.ShopStocked = shopStocked;
        Raise(OwnedItemChangeKind.ShopStocked, entry, entry.Where);
        return true;
    }

    /// <summary>
    /// Gives a live essential a claim slot, as when its owner puts it in that slot. A slot holds one
    /// item, so another of the owner's essentials claiming it moves to the highest slot no claim of
    /// theirs holds (DV fills the backpack from the front). Returns every claim that moved.
    /// </summary>
    public IReadOnlyList<OwnedItemClaimMove> SetClaimSlot(Guid id, int slot)
    {
        if (slot < 0 || slot >= claimCapacity || !entries.TryGetValue(id, out OwnedItemEntry entry) ||
            !entry.IsLive || entry.ClaimSlot < 0)
            return Array.Empty<OwnedItemClaimMove>();
        List<OwnedItemClaimMove> moves = new();
        if (entry.ClaimSlot != slot)
        {
            moves.Add(new OwnedItemClaimMove(id, entry.ClaimSlot, slot));
            entry.ClaimSlot = slot;
            Raise(OwnedItemChangeKind.ClaimSlot, entry, entry.Where);
        }
        foreach (OwnedItemEntry other in LiveClaims(entry.Owner).Where(e => e.Id != id && e.ClaimSlot == slot).ToArray())
            moves.Add(MoveClaim(other, FreeClaimSlot(entry.Owner, -1)));
        return moves.Where(move => move.FromSlot != move.ToSlot).ToArray();
    }

    /// <summary>
    /// Leaves each of an owner's essentials on a claim slot of its own, for a save from before the
    /// ledger. Of the claims sharing a slot, the item really in that slot keeps it, then the oldest.
    /// With every slot claimed the clash stays rather than drop a claim.
    /// </summary>
    public IReadOnlyList<OwnedItemClaimMove> ResolveClaimClashes(Guid owner)
    {
        List<OwnedItemClaimMove> moves = new();
        foreach (IGrouping<int, OwnedItemEntry> slot in LiveClaims(owner).GroupBy(e => e.ClaimSlot).ToArray())
        {
            OwnedItemEntry[] displaced = slot
                .OrderByDescending(e => e.Where == OwnedItemWhere.With(owner, OwnedItemHold.Inventory, slot.Key))
                .ThenBy(e => e.Born.UtcTicks)
                .ThenBy(e => e.Id)
                .Skip(1)
                .ToArray();
            foreach (OwnedItemEntry entry in displaced)
            {
                int free = FreeClaimSlot(owner, -1);
                if (free < 0)
                    return moves;
                moves.Add(MoveClaim(entry, free));
            }
        }
        return moves;
    }

    public bool TryGet(Guid id, out OwnedItemEntry entry)
    {
        if (id != Guid.Empty && entries.TryGetValue(id, out OwnedItemEntry stored))
        {
            entry = stored.Clone();
            return true;
        }
        entry = null;
        return false;
    }

    public bool IsLive(Guid id) => entries.TryGetValue(id, out OwnedItemEntry entry) && entry.IsLive;

    /// <summary>The owner of a live item, or empty: an unowned item has no entry.</summary>
    public Guid OwnerOf(Guid id) => entries.TryGetValue(id, out OwnedItemEntry entry) && entry.IsLive ? entry.Owner : Guid.Empty;

    public IReadOnlyList<OwnedItemEntry> LiveOwnedBy(Guid owner) => Ordered(entries.Values
        .Where(entry => entry.IsLive && entry.Owner == owner));

    /// <summary>Everything a player has with them, theirs or not: what they get back when they join.</summary>
    public IReadOnlyList<OwnedItemEntry> LiveWith(Guid holder) => Ordered(entries.Values
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
    public IReadOnlyList<OwnedItemEntry> All() => Ordered(entries.Values);

    public bool HasStarterKit(Guid player) => starterKitsGiven.Contains(player);

    public void MarkStarterKit(Guid player)
    {
        if (player != Guid.Empty)
            starterKitsGiven.Add(player);
    }

    public IReadOnlyCollection<Guid> StarterKitsGiven => starterKitsGiven;

    /// <summary>
    /// Replaces the ledger with saved entries, raising nothing. An id saved twice keeps its first
    /// entry. Returns the entries it refused.
    /// </summary>
    public IReadOnlyList<OwnedItemEntry> Restore(IEnumerable<OwnedItemEntry> saved, IEnumerable<Guid> starterKits)
    {
        entries.Clear();
        starterKitsGiven.Clear();
        List<OwnedItemEntry> refused = new();
        foreach (OwnedItemEntry entry in saved ?? Enumerable.Empty<OwnedItemEntry>())
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

    private IEnumerable<OwnedItemEntry> LiveClaims(Guid owner) =>
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

    private OwnedItemClaimMove MoveClaim(OwnedItemEntry entry, int slot)
    {
        OwnedItemClaimMove move = new(entry.Id, entry.ClaimSlot, slot < 0 ? entry.ClaimSlot : slot);
        if (slot >= 0 && slot != entry.ClaimSlot)
        {
            entry.ClaimSlot = slot;
            Raise(OwnedItemChangeKind.ClaimSlot, entry, entry.Where);
        }
        return move;
    }

    private void PruneEnded(Guid owner)
    {
        OwnedItemEntry[] ended = entries.Values
            .Where(entry => !entry.IsLive && entry.Owner == owner)
            .OrderByDescending(entry => entry.Ended.UtcTicks)
            .ThenBy(entry => entry.Id)
            .ToArray();
        foreach (OwnedItemEntry old in ended.Skip(EndedKeptPerOwner))
            entries.Remove(old.Id);
    }

    private static IReadOnlyList<OwnedItemEntry> Ordered(IEnumerable<OwnedItemEntry> source) => source
        .OrderBy(entry => entry.Owner)
        .ThenBy(entry => entry.Born.UtcTicks)
        .ThenBy(entry => entry.Id)
        .Select(entry => entry.Clone())
        .ToArray();

    private void Raise(OwnedItemChangeKind kind, OwnedItemEntry entry, OwnedItemWhere previous) =>
        Changed?.Invoke(new OwnedItemChange(kind, entry.Clone(), previous));
}
