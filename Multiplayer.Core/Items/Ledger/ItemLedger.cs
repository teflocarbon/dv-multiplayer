using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Inventory;

namespace Multiplayer.Core.Items.Ledger;

/// <summary>What the server asks the ledger to record when an item becomes owned.</summary>
public sealed class BirthRequest
{
    /// <summary>The item's persistent id. Empty: the ledger mints one.</summary>
    public Guid Id;
    public OwnerRef Owner;
    public string Prefab;
    public Home Home;
    public BirthReason Reason;
    public Guid By;
    public Guid From;
    public string Detail;
    /// <summary>The item is an essential: it gets a claim slot of its own.</summary>
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
    Invalid,
    /// <summary>Its home is taken (another item is in that slot): nothing was recorded.</summary>
    Refused
}

public readonly struct Registration
{
    public Registration(RegistrationStatus status, LedgerEntry entry, Guid copiedId, string refusal = null)
    {
        Status = status;
        Entry = entry;
        CopiedId = copiedId;
        Refusal = refusal ?? string.Empty;
    }

    public RegistrationStatus Status { get; }
    /// <summary>A copy of the entry as recorded, or null when nothing was.</summary>
    public LedgerEntry Entry { get; }
    /// <summary>For <see cref="RegistrationStatus.Reminted"/>: the id that was already taken.</summary>
    public Guid CopiedId { get; }
    public string Refusal { get; }

    public bool Accepted => Status is RegistrationStatus.Registered or RegistrationStatus.Reminted;
}

public enum LedgerChangeKind : byte
{
    Born,
    Moved,
    Ended,
    ShopStocked,
    ClaimSlot
}

/// <summary>Why the ledger changed: the reason, the player whose request it was (or nobody), and detail.</summary>
public readonly struct LedgerCause
{
    public LedgerCause(ItemMoveReason reason, Guid by = default, string detail = null)
    {
        Reason = reason;
        By = by;
        Detail = detail ?? string.Empty;
    }

    public ItemMoveReason Reason { get; }
    public Guid By { get; }
    public string Detail { get; }

    public override string ToString() =>
        Reason + (By != Guid.Empty ? $" by {By:D}" : "") + (Detail.Length > 0 ? $" ({Detail})" : "");
}

/// <summary>
/// One change to the ledger, raised as it is committed (docs/design/items/ledger.md, "The ledger
/// decides, live state follows"): the entry after it, the home it came from and why. Entries are copies.
/// </summary>
public readonly struct LedgerChange
{
    public LedgerChange(LedgerChangeKind kind, LedgerEntry entry, Home from, LedgerCause cause = default)
    {
        Kind = kind;
        Entry = entry;
        From = from;
        Cause = cause;
    }

    public LedgerChangeKind Kind { get; }
    public LedgerEntry Entry { get; }
    public Home From { get; }
    public LedgerCause Cause { get; }
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

/// <summary>What a <see cref="ItemLedger.Commit"/> did: committed, or refused with nothing changed.</summary>
public readonly struct LedgerCommit
{
    public LedgerCommit(string refusal, LedgerEntry entry, IReadOnlyList<ClaimMove> claimMoves)
    {
        Refusal = refusal ?? string.Empty;
        Entry = entry;
        ClaimMoves = claimMoves ?? Array.Empty<ClaimMove>();
    }

    public bool Committed => Refusal.Length == 0;
    /// <summary>Why it was refused (<c>stale-home</c>, <c>slot-occupied</c>, ...), or empty.</summary>
    public string Refusal { get; }
    /// <summary>The entry after the change (or as it stayed), a copy; null for an unknown item.</summary>
    public LedgerEntry Entry { get; }
    /// <summary>The claims the change moved: the item's own, and any another essential gave up for it.</summary>
    public IReadOnlyList<ClaimMove> ClaimMoves { get; }

    public static LedgerCommit Refused(string reason, LedgerEntry entry = null) => new(reason, entry, null);
}

/// <summary>What re-keying an identity onto another did (OI-ID-19).</summary>
public sealed class RekeyResult
{
    public int Changed;
    /// <summary>Items whose slot the new identity already used: they went to Lost and Found.</summary>
    public readonly List<Guid> ToLostAndFound = new();
    /// <summary>Claims that moved to a free slot because the new identity already claimed theirs.</summary>
    public readonly List<ClaimMove> ClaimMoves = new();
}

/// <summary>
/// The server's ledger of owned items (docs/design/items/ledger.md), keyed by the item's lifelong
/// id. It alone decides each one's owner and home: a change to an owned item is committed here
/// first, and refused here if it names a home the item isn't in or a slot that is taken; only then
/// does live state apply it. Owners and holders are identities, so an offline owner's items are as
/// owned as anyone's. Pure: no Unity, no sessions, no NetIds.
/// </summary>
public sealed class ItemLedger
{
    /// <summary>Ended entries kept per owner for lineage ("where did my radio go"); older ones are pruned.</summary>
    public const int EndedKeptPerOwner = 100;

    private readonly Dictionary<Guid, LedgerEntry> entries = new();
    private readonly HashSet<Guid> starterKitsGiven = new();
    private readonly Func<Guid> newId;
    private readonly Func<long> utcNow;
    private readonly int capacity;

    public ItemLedger(Func<Guid> newId = null, Func<long> utcNow = null, int capacity = InventorySlots.Capacity)
    {
        this.newId = newId ?? Guid.NewGuid;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow.Ticks);
        this.capacity = capacity;
    }

    public event Action<LedgerChange> Changed;

    /// <summary>
    /// Whether something the ledger doesn't hold (an unowned item, from live state) is in a holder's
    /// slot, other than the item named. The server sets it; without it only owned items count.
    /// </summary>
    public Func<Guid, int, Guid, bool> SlotTakenOutside { get; set; }

    public int Count => entries.Count;

    /// <summary>
    /// Records an item becoming owned. An id is for life, so a request naming an id the ledger already
    /// has, live or ended, is a copy (a copied save, a dupe, an object cloned with its identity): the
    /// first keeps the id and the copy is recorded under a new one, born <see cref="BirthReason.Duplicate"/>.
    /// A home whose slot is taken is refused, and nothing is recorded.
    /// </summary>
    public Registration Register(BirthRequest request)
    {
        if (request == null || request.Owner.IsNone || string.IsNullOrEmpty(request.Prefab))
            return new Registration(RegistrationStatus.Invalid, null, Guid.Empty);
        // The item itself may already sit in that slot (its record, unowned until now); a copy of a
        // live item is checked against the original, which keeps its slot.
        string taken = SlotRefusal(request.Home, entries.ContainsKey(request.Id) ? Guid.Empty : request.Id, request.Owner);
        if (taken.Length > 0)
            return new Registration(RegistrationStatus.Refused, null, Guid.Empty, taken);

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

        LedgerEntry entry = new(id, request.Owner, request.Prefab, request.Home,
            new Birth(reason, utcNow(), request.By, from, detail), -1, request.ShopStocked,
            essential: request.Essential);
        entries.Add(id, entry);
        if (entry.Essential)
        {
            // The slot asked for, else the owner's slot it is in, else the lowest free one.
            HashSet<int> claimed = ClaimedSlots(entry.Owner, id);
            bool Taken(int slot) => Occupied(entry.Owner.PlayerId, slot, id);
            int own = OwnersSlot(entry, request.Home);
            int claim = InventorySlots.ChooseClaim(request.PreferredClaimSlot, claimed, Taken, capacity);
            if (claim != request.PreferredClaimSlot && own >= 0)
                claim = InventorySlots.ChooseClaim(own, claimed, Taken, capacity);
            entry.ClaimSlot = claim;
        }
        Raise(LedgerChangeKind.Born, entry, request.Home, new LedgerCause(ItemMoveReason.Unknown, request.By, detail));
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

    /// <summary>
    /// Moves a live item from <paramref name="from"/> to <paramref name="to"/>, the one way an owned
    /// item's home changes. Refused, with nothing changed, when the item isn't in
    /// <paramref name="from"/> (a stale or repeated request), or <paramref name="to"/> is a slot that
    /// is out of range, holds another item or shows another essential's claim. An essential its owner
    /// puts in a slot off the belt claims that slot; an essential that claimed it before gives it up
    /// for this one's old claim, or the lowest free one.
    /// </summary>
    public LedgerCommit Commit(Guid id, Home from, Home to, LedgerCause cause = default)
    {
        if (!entries.TryGetValue(id, out LedgerEntry entry) || !entry.IsLive)
            return LedgerCommit.Refused("unknown-item", entry?.Clone());
        if (entry.Home != from)
            return LedgerCommit.Refused("stale-home", entry.Clone());
        return Place(entry, to, cause);
    }

    /// <summary>
    /// Puts a live item in <paramref name="to"/> whatever its home was: for the load, the invariant
    /// check's repair and the server's own recovery, never for a player's request. The slot rules
    /// still hold.
    /// </summary>
    public LedgerCommit Repair(Guid id, Home to, LedgerCause cause = default)
    {
        if (!entries.TryGetValue(id, out LedgerEntry entry) || !entry.IsLive)
            return LedgerCommit.Refused("unknown-item", entry?.Clone());
        return Place(entry, to, cause);
    }

    /// <summary>
    /// Two or more of an owner's live items claim one slot (the one-home check's claim clash, D7):
    /// the first registered (born first, then by id) keeps it, and each other one takes the owner's
    /// next free claim slot, or no claim when none is free. Each moved claim is raised as a
    /// <see cref="LedgerChangeKind.ClaimSlot"/> change, which tells the owner's game.
    /// </summary>
    public IReadOnlyList<ClaimMove> SettleClaimClash(OwnerRef owner, int slot, LedgerCause cause = default)
    {
        List<ClaimMove> moves = new();
        LedgerEntry[] clash = LiveClaims(owner).Where(entry => entry.ClaimSlot == slot)
            .OrderBy(entry => entry.Born.UtcTicks).ThenBy(entry => entry.Id).ToArray();
        foreach (LedgerEntry other in clash.Skip(1))
        {
            int free = InventorySlots.ChooseClaim(-1, ClaimedSlots(owner, other.Id),
                s => Occupied(owner.PlayerId, s, other.Id), capacity);
            moves.Add(new ClaimMove(other.Id, slot, free));
            other.ClaimSlot = free;
            Raise(LedgerChangeKind.ClaimSlot, other, other.Home, cause);
        }
        return moves;
    }

    /// <summary>
    /// Two items in one holder's inventory change slots (vanilla's swap), in one commit, since
    /// each slot holds one item. Refused, with nothing changed, when either isn't where the
    /// requester saw it, the homes aren't two slots of one holder, or either slot shows a third
    /// item's claim. The claims follow as <see cref="Commit"/>'s do.
    /// </summary>
    public LedgerCommit Swap(Guid id, Home from, Guid otherId, Home otherFrom, LedgerCause cause = default)
    {
        if (!entries.TryGetValue(id, out LedgerEntry entry) || !entry.IsLive ||
            !entries.TryGetValue(otherId, out LedgerEntry other) || !other.IsLive || id == otherId)
            return LedgerCommit.Refused("unknown-item", entry?.Clone());
        if (entry.Home != from || other.Home != otherFrom)
            return LedgerCommit.Refused("stale-home", entry.Clone());
        if (from.Kind != HomeKind.Slot || otherFrom.Kind != HomeKind.Slot || from.Holder != otherFrom.Holder)
            return LedgerCommit.Refused("slot-invalid", entry.Clone());
        if (ReservedByThird(otherFrom, id, otherId) || ReservedByThird(from, id, otherId))
            return LedgerCommit.Refused("slot-reserved", entry.Clone());
        entry.Home = otherFrom;
        other.Home = from;
        List<ClaimMove> moves = FollowClaim(entry, otherFrom);
        moves.AddRange(FollowClaim(other, from));
        Raise(LedgerChangeKind.Moved, entry, from, cause);
        Raise(LedgerChangeKind.Moved, other, otherFrom, cause);
        foreach (ClaimMove move in moves.Where(move => move.Id != entry.Id && move.Id != other.Id))
            Raise(LedgerChangeKind.ClaimSlot, entries[move.Id], entries[move.Id].Home, cause);
        return new LedgerCommit(null, entry.Clone(), moves);
    }

    // A slot another of the holder's essentials, neither of these two, claims from outside it.
    private bool ReservedByThird(Home slot, Guid id, Guid otherId) => entries.Values.Any(third =>
        third.IsLive && third.Id != id && third.Id != otherId && third.ClaimSlot == slot.Slot &&
        third.Owner.IsPlayer(slot.Holder) && !(third.Home.Kind == HomeKind.Slot && third.Home.Holder == slot.Holder));

    private LedgerCommit Place(LedgerEntry entry, Home to, LedgerCause cause)
    {
        string refusal = SlotRefusal(to, entry.Id, entry.Owner);
        if (refusal.Length > 0)
            return LedgerCommit.Refused(refusal, entry.Clone());
        if (entry.Home == to)
            return new LedgerCommit(null, entry.Clone(), null);
        Home previous = entry.Home;
        entry.Home = to;
        List<ClaimMove> moves = FollowClaim(entry, to);
        Raise(LedgerChangeKind.Moved, entry, previous, cause);
        foreach (ClaimMove move in moves.Where(move => move.Id != entry.Id))
            Raise(LedgerChangeKind.ClaimSlot, entries[move.Id], entries[move.Id].Home, cause);
        return new LedgerCommit(null, entry.Clone(), moves);
    }

    // The owner put their essential in a slot off the belt: that's the slot it comes back to.
    private List<ClaimMove> FollowClaim(LedgerEntry entry, Home to)
    {
        List<ClaimMove> moves = new();
        int slot = OwnersSlot(entry, to);
        if (!entry.Essential || slot < 0 || InventorySlots.IsBelt(slot) || entry.ClaimSlot == slot)
            return moves;
        int old = entry.ClaimSlot;
        LedgerEntry displaced = LiveClaims(entry.Owner).FirstOrDefault(other => other.Id != entry.Id && other.ClaimSlot == slot);
        if (displaced != null)
        {
            int next = old >= 0 ? old : InventorySlots.ChooseClaim(-1, ClaimedSlots(entry.Owner, displaced.Id, entry.Id),
                s => s == slot || Occupied(entry.Owner.PlayerId, s, displaced.Id), capacity);
            if (next < 0)
                return moves;
            displaced.ClaimSlot = next;
            moves.Add(new ClaimMove(displaced.Id, slot, next));
        }
        entry.ClaimSlot = slot;
        moves.Insert(0, new ClaimMove(entry.Id, old, slot));
        return moves;
    }

    /// <summary>Ends a live item's life. Its entry stays, for lineage, until pruned.</summary>
    public bool End(Guid id, ItemRemovalReason reason, Guid replacedBy = default, string detail = null)
    {
        if (!entries.TryGetValue(id, out LedgerEntry entry) || !entry.IsLive)
            return false;
        entry.Ended = new End(reason, utcNow(), replacedBy, detail);
        Raise(LedgerChangeKind.Ended, entry, entry.Home);
        PruneEnded(entry.Owner);
        return true;
    }

    /// <summary>
    /// Brings an ended entry back, for a load that finds its item in a store after all (a build
    /// without the ledger kept playing the save). Raised as <see cref="LedgerChangeKind.Born"/>,
    /// so anything counting live items counts it again.
    /// </summary>
    public bool Revive(Guid id, Home home)
    {
        if (!entries.TryGetValue(id, out LedgerEntry entry) || entry.IsLive || SlotRefusal(home, id, entry.Owner).Length > 0)
            return false;
        Home previous = entry.Home;
        entry.Ended = null;
        entry.Home = home;
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
        Raise(LedgerChangeKind.ShopStocked, entry, entry.Home);
        return true;
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

    /// <summary>The owner of a live item, or none: an unowned item has no entry.</summary>
    public OwnerRef OwnerOf(Guid id) => id != Guid.Empty && entries.TryGetValue(id, out LedgerEntry entry) && entry.IsLive
        ? entry.Owner : OwnerRef.None;

    /// <summary>A live item's home, or false.</summary>
    public bool TryGetHome(Guid id, out Home home)
    {
        bool live = id != Guid.Empty && entries.TryGetValue(id, out LedgerEntry entry) && entry.IsLive;
        home = live ? entries[id].Home : default;
        return live;
    }

    /// <summary>The live claim slot of an item, or -1.</summary>
    public int ClaimOf(Guid id) => id != Guid.Empty && entries.TryGetValue(id, out LedgerEntry entry) && entry.IsLive
        ? entry.ClaimSlot : -1;

    public IReadOnlyList<LedgerEntry> LiveOwnedBy(OwnerRef owner) => Ordered(entries.Values
        .Where(entry => entry.IsLive && entry.Owner == owner));

    /// <summary>Everything a player has with them, theirs or not: what they get back when they join.</summary>
    public IReadOnlyList<LedgerEntry> LiveWith(Guid holder) => Ordered(entries.Values
        .Where(entry => entry.IsLive && entry.Home.IsWithPlayer && entry.Home.Holder == holder));

    /// <summary>The owned item in a holder's slot, or empty.</summary>
    public Guid OccupantOf(Guid holder, int slot) => entries.Values
        .FirstOrDefault(entry => entry.IsLive && entry.Home.Kind == HomeKind.Slot &&
            entry.Home.Holder == holder && entry.Home.Slot == slot)?.Id ?? Guid.Empty;

    /// <summary>The slots an owner's essentials claim, but for the items named.</summary>
    public HashSet<int> ClaimedSlots(OwnerRef owner, params Guid[] except) =>
        new(LiveClaims(owner).Where(entry => !except.Contains(entry.Id)).Select(entry => entry.ClaimSlot));

    /// <summary>The slot a new essential of this owner would claim (the issuer asks, so both agree: OI-ESS-02).</summary>
    public int ChooseClaimSlot(OwnerRef owner, int preferred = -1) =>
        InventorySlots.ChooseClaim(preferred, ClaimedSlots(owner), slot => Occupied(owner.PlayerId, slot, Guid.Empty), capacity);

    /// <summary>
    /// How many live items of a prefab an owner has: for issuing essentials (any), and for shop stock
    /// (<paramref name="shopStocked"/> true counts only items still carrying vanilla's Restock flag).
    /// </summary>
    public int CountLive(OwnerRef owner, string prefab, bool? shopStocked = null) => entries.Values.Count(entry =>
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
            entry.Owner.IsPlayer(player) || entry.Home.IsWithPlayer && entry.Home.Holder == player ||
            entry.Born.By == player));

    /// <summary>
    /// A player's identity changed (an install GUID re-keyed to their Steam account, OI-ID-19): what
    /// they own, what they have with them, what they caused and their starter kit merge into the new
    /// identity, ended entries included. When the new identity already has data, nothing is skipped:
    /// an item in a slot the new identity already fills goes to Lost and Found, and a claim on a slot
    /// it already claims moves to a free one (or none). Raises nothing.
    /// </summary>
    public RekeyResult Rekey(Guid from, Guid to)
    {
        RekeyResult result = new();
        if (from == Guid.Empty || to == Guid.Empty || from == to)
            return result;
        OwnerRef newOwner = OwnerRef.Player(to);
        foreach (LedgerEntry entry in entries.Values.OrderBy(entry => entry.Born.UtcTicks).ThenBy(entry => entry.Id).ToArray())
        {
            bool touched = false;
            if (entry.Owner.IsPlayer(from))
            {
                entry.Owner = newOwner;
                touched = true;
                if (entry.IsLive && entry.ClaimSlot >= 0 && ClaimedSlots(newOwner, entry.Id).Contains(entry.ClaimSlot))
                {
                    int free = InventorySlots.ChooseClaim(-1, ClaimedSlots(newOwner, entry.Id), slot => Occupied(to, slot, entry.Id), capacity);
                    result.ClaimMoves.Add(new ClaimMove(entry.Id, entry.ClaimSlot, free));
                    entry.ClaimSlot = free;
                }
            }
            if (entry.Home.IsWithPlayer && entry.Home.Holder == from)
            {
                Home moved = entry.Home.Kind == HomeKind.Slot ? Home.InSlot(to, entry.Home.Slot) : Home.InHand(to, entry.Home.Hand);
                if (entry.IsLive && moved.Kind == HomeKind.Slot && SlotRefusal(moved, entry.Id, entry.Owner) == "slot-occupied")
                {
                    entry.Home = Home.LostAndFound;
                    result.ToLostAndFound.Add(entry.Id);
                }
                else
                    entry.Home = moved;
                touched = true;
            }
            if (entry.Born.By == from)
            {
                entry.Born = new Birth(entry.Born.Reason, entry.Born.UtcTicks, to, entry.Born.From, entry.Born.Detail);
                touched = true;
            }
            if (touched)
                result.Changed++;
        }
        if (starterKitsGiven.Remove(from))
            starterKitsGiven.Add(to);
        return result;
    }

    /// <summary>
    /// Replaces the ledger with saved entries, raising nothing. An id saved twice keeps its first
    /// entry; the rest are returned as refused. Two live entries saved in one slot keep the first
    /// there and send the others to Lost and Found (<paramref name="displaced"/>), and claims that
    /// clash move apart, so the ledger never starts out breaking its own rules.
    /// </summary>
    public IReadOnlyList<LedgerEntry> Restore(IEnumerable<LedgerEntry> saved, IEnumerable<Guid> starterKits,
        List<Guid> displaced = null)
    {
        entries.Clear();
        starterKitsGiven.Clear();
        List<LedgerEntry> refused = new();
        foreach (LedgerEntry entry in saved ?? Enumerable.Empty<LedgerEntry>())
        {
            if (entry == null || entry.Id == Guid.Empty || entry.Owner.IsNone || entries.ContainsKey(entry.Id))
            {
                refused.Add(entry);
                continue;
            }
            LedgerEntry copy = entry.Clone();
            if (copy.IsLive && copy.Home.Kind == HomeKind.Slot && SlotRefusal(copy.Home, copy.Id, copy.Owner) == "slot-occupied")
            {
                copy.Home = Home.LostAndFound;
                displaced?.Add(copy.Id);
            }
            if (copy.IsLive && copy.ClaimSlot >= 0 && ClaimedSlots(copy.Owner).Contains(copy.ClaimSlot))
                copy.ClaimSlot = InventorySlots.ChooseClaim(-1, ClaimedSlots(copy.Owner),
                    slot => Occupied(copy.Owner.PlayerId, slot, copy.Id), capacity);
            entries.Add(copy.Id, copy);
        }
        foreach (Guid player in starterKits ?? Enumerable.Empty<Guid>())
            MarkStarterKit(player);
        return refused;
    }

    /// <summary>The ledger, one line per entry (live first): for the debug console and <c>Ledger.Dump()</c> (OI-LED-13).</summary>
    public string Dump(OwnerRef owner = default)
    {
        StringBuilder text = new();
        IEnumerable<LedgerEntry> shown = Ordered(entries.Values.Where(entry => owner.IsNone || entry.Owner == owner))
            .OrderBy(entry => entry.IsLive ? 0 : 1);
        foreach (LedgerEntry entry in shown)
            text.Append(entry).Append(" | born ").Append(entry.Born.Reason)
                .Append(entry.Born.From != Guid.Empty ? $" from {entry.Born.From:D}" : "").Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Why <paramref name="id"/> can't be put in <paramref name="home"/>, or empty: the slot rules every
    /// commit checks, for an unowned item the ledger doesn't hold (OI-SLOT-03).
    /// </summary>
    public string CanTake(Home home, Guid id, OwnerRef owner = default) => SlotRefusal(home, id, owner);

    // Why a home can't be taken by the item named: an inventory slot out of range, another item in
    // it, or another of the holder's essentials claiming it (its silhouette fills the slot).
    private string SlotRefusal(Home home, Guid id, OwnerRef owner)
    {
        if (home.Kind != HomeKind.Slot)
            return string.Empty;
        if (home.Holder == Guid.Empty || !InventorySlots.IsValid(home.Slot, capacity))
            return "slot-invalid";
        if (OccupiedByEntry(home.Holder, home.Slot, id) || SlotTakenOutside?.Invoke(home.Holder, home.Slot, id) == true)
            return "slot-occupied";
        if (entries.Values.Any(other => other.IsLive && other.Id != id && other.ClaimSlot == home.Slot &&
                other.Owner.IsPlayer(home.Holder) && !(other.Home.Kind == HomeKind.Slot && other.Home.Holder == home.Holder)))
            return "slot-reserved";
        return string.Empty;
    }

    private bool OccupiedByEntry(Guid holder, int slot, Guid except) => entries.Values.Any(entry =>
        entry.IsLive && entry.Id != except && entry.Home.Kind == HomeKind.Slot &&
        entry.Home.Holder == holder && entry.Home.Slot == slot);

    private bool Occupied(Guid holder, int slot, Guid except) => holder != Guid.Empty &&
        (OccupiedByEntry(holder, slot, except) || SlotTakenOutside?.Invoke(holder, slot, except) == true);

    // The slot an item is in when its home is its own owner's slot, else -1.
    private static int OwnersSlot(LedgerEntry entry, Home home) =>
        home.Kind == HomeKind.Slot && entry.Owner.IsPlayer(home.Holder) ? home.Slot : -1;

    private Guid Mint()
    {
        Guid id;
        do id = newId();
        while (id == Guid.Empty || entries.ContainsKey(id));
        return id;
    }

    private IEnumerable<LedgerEntry> LiveClaims(OwnerRef owner) =>
        entries.Values.Where(entry => entry.IsLive && entry.Owner == owner && entry.ClaimSlot >= 0);

    private void PruneEnded(OwnerRef owner)
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
        .OrderBy(entry => entry.Owner.Id)
        .ThenBy(entry => entry.Born.UtcTicks)
        .ThenBy(entry => entry.Id)
        .Select(entry => entry.Clone())
        .ToArray();

    private void Raise(LedgerChangeKind kind, LedgerEntry entry, Home from, LedgerCause cause = default) =>
        Changed?.Invoke(new LedgerChange(kind, entry.Clone(), from, cause));
}
