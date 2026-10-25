using DV.Shops;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Inventory;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Core.Items.SaveLoad;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Multiplayer.Server.Items.Ledger;

/// <summary>
/// The server's ledger of owned items (docs/design/items/ledger.md): who owns each one and its one
/// exact home. It decides: an owned item's change is a ledger commit first, and live state applies
/// only what the ledger committed (<see cref="CommitTransition"/>). Owners and claim slots are read
/// from it, never stored beside it. The load (<see cref="ItemSaveLoad"/>) hands it the saved entries
/// before it makes any item, and tells it when every item is made (<see cref="Loaded"/>).
/// </summary>
public static partial class ServerLedger
{
    private static ItemLedger ledger = NewLedger();
    private static readonly Dictionary<Guid, ushort> netIdByPersistentId = new();
    private static bool restored;
    private static bool loaded;

    public static ItemLedger Ledger => ledger;

    /// <summary>The saved entries are in: owners and claims come from the ledger.</summary>
    public static bool Restored => restored;

    /// <summary>The saved ledger has been checked against the stores.</summary>
    public static bool Loaded => loaded;

    public static void Clear()
    {
        ledger = NewLedger();
        netIdByPersistentId.Clear();
        restored = false;
        loaded = false;
        ClearInvariant();
    }

    private static ItemLedger NewLedger()
    {
        ItemLedger made = new() { SlotTakenOutside = UnownedInSlot };
        made.Changed += OnLedgerChanged;
        return made;
    }

    public static void HostTick()
    {
        if (NetworkLifecycle.Instance?.IsHost() == true)
            InvariantTick();
    }

    /// <summary>The owner of the item with this id: the ledger's; before its saved entries are in, the owner the item's store named.</summary>
    public static Guid OwnerOf(Guid id, Guid storedOwner = default) =>
        id == Guid.Empty ? Guid.Empty : restored ? ledger.OwnerOf(id).PlayerId : storedOwner;

    /// <summary>An essential's claim slot from the ledger, or -1; before its saved entries are in, the slot the store named.</summary>
    public static int ClaimOf(Guid id, int storedClaim = -1) =>
        id == Guid.Empty ? -1 : restored ? ledger.ClaimOf(id) : storedClaim;

    /// <summary>
    /// An id is for life and names one item. An item registering with an id another live item has
    /// (a copied save, a dupe, an object cloned with its identity) keeps the first where it is and
    /// gets a new id; its entry, once it has an owner, is born <see cref="BirthReason.Duplicate"/>
    /// from the first (OI-ID-05, -18). Returns the id the item is to have.
    /// </summary>
    internal static Guid ClaimPersistentId(NetworkedItem item, Guid id)
    {
        if (item == null || id == Guid.Empty)
            return id;
        if (netIdByPersistentId.TryGetValue(id, out ushort other) && other != item.NetId &&
            LiveItems.TryGet(other, out LiveItems.LiveItem original) &&
            original.PersistentItemId == id && NetworkedItem.TryGet(other, out NetworkedItem originalItem) &&
            originalItem != null)
        {
            Guid copy = Guid.NewGuid();
            Multiplayer.LogError($"item.duplicate-persistent-id: {item.name} (netId {item.NetId}) arrived with id {id:D}, " +
                                 $"which {originalItem.name} (netId {other}, {original.Placement}) has; " +
                                 $"the first keeps it and the copy is now {copy:D}");
            ItemIdentity identity = item.GetComponent<ItemIdentity>() ??
                                                   item.gameObject.AddComponent<ItemIdentity>();
            identity.Value = copy;
            identity.CopiedFrom = id;
            id = copy;
        }
        netIdByPersistentId[id] = item.NetId;
        return id;
    }

    /// <summary>An item kept as data from the start (no object to claim its id): its NetId by its id.</summary>
    internal static void BindNetId(Guid id, ushort netId)
    {
        if (id != Guid.Empty && netId != 0)
            netIdByPersistentId[id] = netId;
    }

    /// <summary>
    /// Commits an accepted transition's change of home before live state applies it (OI-LED-04):
    /// an owned item moves in the ledger, from where the record had it; an item a player takes
    /// first becomes theirs (<paramref name="newOwner"/>, born <see cref="BirthReason.Claimed"/>);
    /// an unowned item is held to the slot rules only. Returns why it's refused, or empty, and
    /// changes nothing when it refuses.
    /// </summary>
    internal static string CommitTransition(LiveItems.LiveItem record, Home from, Home to, Guid newOwner,
        ChangeCause<ItemMoveReason> cause)
    {
        if (!restored || record == null)
            return string.Empty;
        Guid id = record.PersistentItemId;
        if (id != Guid.Empty && ledger.IsLive(id))
            return Commit(record, from, to, cause);
        if (newOwner != Guid.Empty && id != Guid.Empty)
            return Own(record, newOwner, BirthFrom(cause.Reason), PlayerNumbers.Identity(cause.PlayerId),
                cause.Detail, to);
        return ledger.CanTake(to, id);
    }

    /// <summary>
    /// Moves an owned item in the ledger: the one way its home changes. Empty when it moved or
    /// isn't owned; else why not. A record that disagreed with the ledger is logged and the ledger
    /// moved anyway, so a drift found here never wedges the item.
    /// </summary>
    internal static string Commit(LiveItems.LiveItem record, Home from, Home to, ChangeCause<ItemMoveReason> cause)
    {
        Guid id = record?.PersistentItemId ?? Guid.Empty;
        if (!restored || id == Guid.Empty || !ledger.IsLive(id) || to.Kind == HomeKind.Unloadable)
            return string.Empty;
        LedgerCause because = new(cause.Reason, PlayerNumbers.Identity(cause.PlayerId), cause.Detail);
        LedgerCommit commit = ledger.Commit(id, from, to, because);
        if (commit.Refusal == "stale-home")
        {
            ledger.TryGetHome(id, out Home had);
            Multiplayer.LogError($"item.ledger-stale-home: {record.PrefabName} (netId {record.NetId}, {id:D}) " +
                                 $"was {from} by its record but {had} by the ledger; moved to {to} ({cause})");
            commit = ledger.Repair(id, to, because);
        }
        return commit.Committed ? string.Empty : commit.Refusal;
    }

    /// <summary>
    /// Two items in one inventory changed slots (vanilla's swap); their records already have the
    /// new slots. The owned ones move in the ledger: both in one commit, or the one alone, since
    /// the ledger reads an unowned item's slot from its record. Empty when it's done; else why not.
    /// </summary>
    internal static string Swap(LiveItems.LiveItem record, Home from, LiveItems.LiveItem other, Home otherFrom,
        ChangeCause<ItemMoveReason> cause)
    {
        if (!restored)
            return string.Empty;
        bool owned = ledger.IsLive(record.PersistentItemId), otherOwned = ledger.IsLive(other.PersistentItemId);
        if (!owned || !otherOwned)
            return owned ? Commit(record, from, HomeOf(record), cause)
                : otherOwned ? Commit(other, otherFrom, HomeOf(other), cause)
                : ledger.CanTake(HomeOf(record), record.PersistentItemId);
        LedgerCommit commit = ledger.Swap(record.PersistentItemId, from, other.PersistentItemId, otherFrom,
            new LedgerCause(cause.Reason, PlayerNumbers.Identity(cause.PlayerId), cause.Detail));
        return commit.Committed ? string.Empty : commit.Refusal;
    }

    /// <summary>
    /// Makes an item owned (a purchase, an issue, an admin's give, a first take, a gadget's saved
    /// owner): its entry is born here, in <paramref name="home"/> or where its record has it. Empty
    /// when it's owned, by this owner or already by someone; else why not.
    /// </summary>
    internal static string Own(LiveItems.LiveItem record, Guid owner, BirthReason reason, Guid by, string detail,
        Home? home = null, int preferredClaim = -1)
    {
        if (record == null || owner == Guid.Empty || record.PersistentItemId == Guid.Empty)
            return "item-has-no-id";
        // A job paper is its job's, never owned or in the ledger (D5, OI-ID-12): nothing to own.
        if (Shared.Jobs.JobPapers.IsPaper(record.PrefabName))
            return string.Empty;
        if (ledger.IsLive(record.PersistentItemId))
            return string.Empty;
        if (!restored)
        {
            record.StoredOwner = owner;
            return string.Empty;
        }
        NetworkedItem.TryGet(record.NetId, out NetworkedItem item);
        Guid copiedFrom = item != null ? item.GetComponent<ItemIdentity>()?.CopiedFrom ?? Guid.Empty : Guid.Empty;
        Registration registered = ledger.Register(new BirthRequest
        {
            Id = record.PersistentItemId,
            Owner = OwnerRef.Player(owner),
            Prefab = record.PrefabName,
            Home = home ?? HomeOf(record),
            Reason = copiedFrom != Guid.Empty ? BirthReason.Duplicate : reason,
            By = by,
            From = copiedFrom,
            Detail = copiedFrom != Guid.Empty ? $"a copy of {copiedFrom:D}, {reason}" : detail,
            Essential = item != null ? item.Item?.InventorySpecs?.IsEssential == true
                : ItemSaveLoad.FindSpec(record.PrefabName)?.IsEssential == true,
            PreferredClaimSlot = preferredClaim,
            ShopStocked = IsShopStocked(record.NetId)
        });
        if (!registered.Accepted)
            return registered.Refusal ?? "invalid";
        record.StoredOwner = Guid.Empty;
        if (registered.Status == RegistrationStatus.Reminted)
            TakeNewId(record, registered);
        return string.Empty;
    }

    /// <summary>
    /// The server's own placing of an item it restores or makes (Lost and Found, a withdrawal, a
    /// load): an owned item's entry goes to where the record now has it, whatever its home was; an
    /// item not yet in the ledger is born there, <paramref name="owner"/>'s. Logged when refused.
    /// </summary>
    internal static void Settle(LiveItems.LiveItem record, Guid owner, int preferredClaim, ChangeCause<ItemMoveReason> cause)
    {
        if (record == null || record.PersistentItemId == Guid.Empty)
            return;
        string refusal;
        if (restored && ledger.IsLive(record.PersistentItemId))
        {
            LedgerCommit commit = ledger.Repair(record.PersistentItemId, HomeOf(record),
                new LedgerCause(cause.Reason, PlayerNumbers.Identity(cause.PlayerId), cause.Detail));
            refusal = commit.Refusal;
        }
        else
            refusal = owner == Guid.Empty ? string.Empty
                : Own(record, owner, BirthFrom(cause.Reason), PlayerNumbers.Identity(cause.PlayerId), cause.Detail,
                    preferredClaim: preferredClaim);
        if (refusal.Length > 0)
            Multiplayer.LogError($"item.ledger-refused: {record.PrefabName} (netId {record.NetId}) to {HomeOf(record)}: {refusal} ({cause})");
    }

    /// <summary>
    /// The slot an item goes into in <paramref name="holder"/>'s inventory: the first of the slots
    /// named that the ledger would take, then its claim (when the holder owns it), then the lowest
    /// free one, the belt last. -1 when the inventory is full.
    /// </summary>
    internal static int FreeSlot(LiveItems.LiveItem record, Guid holder, params int[] preferred) =>
        record == null ? -1 : FreeSlot(record.PersistentItemId, record.Owner, record.InventoryClaimSlot, holder, preferred);

    /// <summary>The same for an item by its id, owner and claim: a cold row's.</summary>
    internal static int FreeSlot(Guid id, Guid ownerId, int claimSlot, Guid holder, params int[] preferred)
    {
        if (holder == Guid.Empty)
            return -1;
        OwnerRef owner = ownerId == Guid.Empty ? OwnerRef.None : OwnerRef.Player(ownerId);
        bool Free(int slot) => InventorySlots.IsValid(slot) &&
            ledger.CanTake(Home.InSlot(holder, slot), id, owner).Length == 0;
        foreach (int slot in preferred)
            if (slot >= 0 && Free(slot))
                return slot;
        int claim = ownerId == holder ? claimSlot : -1;
        if (claim >= 0 && Free(claim))
            return claim;
        return InventorySlots.FirstFree(slot => !Free(slot));
    }

    // A claim the ledger moved as another essential took its slot: the owner's game is told.
    private static void OnLedgerChanged(LedgerChange change)
    {
        if (change.Kind != LedgerChangeKind.ClaimSlot || !netIdByPersistentId.TryGetValue(change.Entry.Id, out ushort netId))
            return;
        LiveItems.SendClaim(netId, -1);
    }

    /// <summary>Host: an accepted move of an item (<see cref="ItemChangeLog.RecordMove"/>): only a check that the ledger was told first.</summary>
    internal static void OnMove(LiveItems.LiveItem record, ChangeCause<ItemMoveReason> cause)
    {
        if (!loaded || record == null || record.PersistentItemId == Guid.Empty)
            return;
        Guid id = record.PersistentItemId;
        if (!ledger.TryGetHome(id, out Home home))
        {
            if (record.StoredOwner != Guid.Empty)
                Own(record, record.StoredOwner, BirthFrom(cause.Reason), PlayerNumbers.Identity(cause.PlayerId), cause.Detail);
            return;
        }
        Home now = HomeOf(record);
        if (now.Kind == HomeKind.Unloadable || HomeInvariant.Agrees(home, now))
            return;
        // A path that moved an owned item without committing it first: follow, and say so loudly.
        Multiplayer.LogError($"item.ledger-not-first: {record.PrefabName} (netId {record.NetId}, {id:D}) moved " +
                             $"{home} -> {now} without a ledger commit ({cause}, callers {ItemChangeCausesCallers()})");
        ledger.Repair(id, now, new LedgerCause(cause.Reason, PlayerNumbers.Identity(cause.PlayerId), cause.Detail));
    }

    /// <summary>
    /// Host: a thrown or dropped item came to rest. Physics decides where it lands, so the ledger
    /// takes that home as it is (the world, a car's interior, a shelf), with no commit first.
    /// </summary>
    internal static void OnSettled(LiveItems.LiveItem record, ChangeCause<ItemMoveReason> cause)
    {
        if (!loaded || record == null || !ledger.TryGetHome(record.PersistentItemId, out Home home))
            return;
        Home now = HomeOf(record);
        if (now.Kind == HomeKind.Unloadable || HomeInvariant.Agrees(home, now))
            return;
        ledger.Repair(record.PersistentItemId, now, new LedgerCause(cause.Reason, PlayerNumbers.Identity(cause.PlayerId), cause.Detail));
    }

    /// <summary>Host: an item is going away (<see cref="ItemChangeLog.RecordRemoval"/>).</summary>
    internal static void OnRemoved(NetworkedItem item, ChangeCause<ItemRemovalReason> cause)
    {
        if (!loaded || item == null || !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record) ||
            !ledger.IsLive(record.PersistentItemId))
            return;
        switch (cause.Reason)
        {
            case ItemRemovalReason.CollectedToLostAndFound:
                ledger.Repair(record.PersistentItemId, Home.LostAndFound,
                    new LedgerCause(ItemMoveReason.CollectedToLostAndFound, detail: cause.Detail));
                return;
            // The deposit committed it first; leaving a player's interest is a client's view; a leaver's
            // inventory going cold keeps its home. An item remade fresh keeps its entry.
            case ItemRemovalReason.DepositedInContainer:
            case ItemRemovalReason.LeftInterest:
            case ItemRemovalReason.WentCold:
            case ItemRemovalReason.RemadeFresh:
                return;
        }
        // Unloading the world or quitting destroys every item without ending any of them.
        if (UnloadWatcher.isUnloading || UnloadWatcher.isQuitting || !item.gameObject.scene.isLoaded)
            return;
        ledger.End(record.PersistentItemId, cause.Reason, detail: cause.Detail);
    }

    /// <summary>
    /// Host: the server made <paramref name="replacement"/> as vanilla used <paramref name="used"/> up
    /// (docs/design/items/ledger.md, "Births and ends"): spent duct tape and its empty roll, an empty
    /// spool, a broken label. The replacement is the used item's owner's, born Replaced, and the used
    /// item's entry ends with replacedBy. Call it before the used item is destroyed. False when the
    /// used item isn't owned.
    /// </summary>
    public static bool RecordReplacement(NetworkedItem used, NetworkedItem replacement, ItemRemovalReason endReason, string detail = null)
    {
        if (!loaded || used == null || replacement == null ||
            !LiveItems.TryGet(used.NetId, out LiveItems.LiveItem usedRecord) ||
            !ledger.TryGet(usedRecord.PersistentItemId, out LedgerEntry usedEntry) || !usedEntry.IsLive)
            return false;
        LiveItems.LiveItem made = LiveItems.Ensure(replacement);
        if (made == null || made.PersistentItemId == Guid.Empty)
            return false;
        if (replacement.Item?.InventorySpecs != null)
            replacement.Item.InventorySpecs.BelongsToPlayer = true;
        Registration registration = ledger.Replace(usedRecord.PersistentItemId, endReason, new BirthRequest
        {
            Id = made.PersistentItemId,
            Prefab = made.PrefabName,
            Home = HomeOf(made),
            Detail = detail ?? "replaced " + usedEntry.Prefab
        });
        return registration.Accepted;
    }

    /// <summary>
    /// Moves an owned item by its id in the ledger, for a container's row, which has no live record
    /// (docs/design/items/containers.md): a deposit, a withdrawal, a move between boxes, and their
    /// rollbacks. Empty when it moved or isn't owned; else why not. A home that disagreed is logged
    /// and the ledger moved anyway, as for a record.
    /// </summary>
    internal static string CommitStored(Guid id, string name, Home from, Home to, ChangeCause<ItemMoveReason> cause)
    {
        if (!restored || id == Guid.Empty || !ledger.IsLive(id))
            return string.Empty;
        LedgerCause because = new(cause.Reason, PlayerNumbers.Identity(cause.PlayerId), cause.Detail);
        LedgerCommit commit = ledger.Commit(id, from, to, because);
        if (commit.Refusal == "stale-home")
        {
            ledger.TryGetHome(id, out Home had);
            Multiplayer.LogError($"item.ledger-stale-home: {name} ({id:D}) was {from} by its container but {had} " +
                                 $"by the ledger; moved to {to} ({cause})");
            commit = ledger.Repair(id, to, because);
        }
        return commit.Committed ? string.Empty : commit.Refusal;
    }

    /// <summary>
    /// The load (<see cref="ItemSaveLoad"/>) hands over the saved entries, before any item is made, so
    /// owners and claims are the ledger's from the first. An id saved twice keeps its first entry;
    /// two entries saved in one slot keep the first there and send the rest to Lost and Found.
    /// </summary>
    internal static void LoadEntries(IEnumerable<LedgerEntry> entries, IEnumerable<Guid> starterKits)
    {
        restored = true;
        List<Guid> displaced = new();
        IReadOnlyList<LedgerEntry> refused = ledger.Restore(entries, starterKits, displaced);
        if (refused.Count > 0 || displaced.Count > 0)
            Multiplayer.LogWarning($"Owned items: skipped {refused.Count} duplicate saved entries; " +
                                   $"{displaced.Count} saved in a taken slot went to Lost and Found");
    }

    /// <summary>The load has made every item it makes: the ledger is checked, and changes count from now.</summary>
    internal static void FinishLoad()
    {
        loaded = true;
        foreach (LiveItems.LiveItem record in LiveItems.GetAll())
            if (ledger.IsLive(record.PersistentItemId))
                record.StoredOwner = Guid.Empty;
        Multiplayer.Log($"Owned items: loaded, {ledger.Count} entries, " +
                        $"{ledger.All().Count(entry => entry.IsLive && entry.Home.Kind == HomeKind.Unloadable)} unloadable");
        CheckInvariant("load");
    }

    /// <summary>The ledger, one line per entry (live first), for the debug console and <c>Ledger.Dump()</c> (OI-LED-13).</summary>
    public static string Dump(Guid owner = default) =>
        ledger.Dump(owner == Guid.Empty ? default : OwnerRef.Player(owner));

    /// <summary>
    /// The one way into an owner's Lost and Found for an owned item by its id (OI-LF-08): a purchase
    /// delivered far away, a deleted car's gadgets, retrieval. The item must be lying somewhere, not
    /// with a player. False, with a reason, when it can't go.
    /// </summary>
    public static bool SendToLostAndFound(Guid itemId, LostItemReason reason, string detail, out string refused)
    {
        refused = string.Empty;
        LiveItems.LiveItem record = itemId == Guid.Empty ? null
            : netIdByPersistentId.TryGetValue(itemId, out ushort netId) && LiveItems.TryGet(netId, out LiveItems.LiveItem known) &&
              known.PersistentItemId == itemId ? known
            : LiveItems.GetAll().FirstOrDefault(r => r.PersistentItemId == itemId);
        if (ledger.TryGetHome(itemId, out Home home) && home.Kind == HomeKind.LostAndFound)
            return true;
        // With a player before the object: one in a remote player's inventory is data on the server.
        if (record == null)
            refused = "unknown-item";
        else if (record.Owner == Guid.Empty)
            refused = "item-has-no-owner";
        else if (record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory)
            refused = "item-is-with-a-player";
        else if (record.Placement == ItemPlacementKind.LostAndFound)
            return true;
        else if (!NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null)
            refused = "unknown-item";
        else if (!LostAndFoundServer.Collect(item, reason, detail))
            refused = "collection-refused";
        return refused.Length == 0;
    }

    /// <summary>Where a record has its item, as an exact home.</summary>
    public static Home HomeOf(LiveItems.LiveItem record) => record.Placement switch
    {
        ItemPlacementKind.PlayerHand => Home.InHand(record.Holder, (byte)record.PlacementHand),
        ItemPlacementKind.PlayerInventory => Home.InSlot(record.Holder, record.Slot),
        ItemPlacementKind.TrainInterior => Home.TrainInterior(record.WorldParentPersistentId),
        ItemPlacementKind.StaticParent => Home.Shelf(record.WorldParentKey),
        ItemPlacementKind.Attached => Home.Attached(record.AttachedCarPersistentId, record.AttachedFront),
        ItemPlacementKind.SnappedAttachment => Home.Snapped(record.WorldParentKey),
        ItemPlacementKind.Magazine => Home.Magazine(record.MagazineToolId, record.MagazineSlot),
        // A gadget on no car is installed on the world (vanilla's WorldCustomization, OI-GAD-10).
        ItemPlacementKind.Installed => Home.Installed(record.AttachedCarNetId == 0 ? Home.WorldAnchor : record.AttachedCarPersistentId),
        ItemPlacementKind.LostAndFound => Home.LostAndFound,
        ItemPlacementKind.World => Home.World,
        // A destroyed or container-placed record has no home of its own here; nothing commits to Unloadable.
        _ => Home.Unloadable
    };

    // An unowned item in a holder's slot: the ledger's slot rules cover items it doesn't hold.
    private static bool UnownedInSlot(Guid holder, int slot, Guid except) => LiveItems.GetAll().Any(record =>
        record.Placement == ItemPlacementKind.PlayerInventory && record.Holder == holder && record.Slot == slot &&
        record.PersistentItemId != except && (record.PersistentItemId == Guid.Empty || !ledger.IsLive(record.PersistentItemId))) ||
        ColdInventories.At(holder, slot) is SavedItemRow row && row.PersistentItemId != except && !ledger.IsLive(row.PersistentItemId);

    private static void TakeNewId(LiveItems.LiveItem record, Registration registered)
    {
        // An ended id came back: the item takes the new one, as a copy does.
        Multiplayer.LogError($"item.duplicate-persistent-id: {record.PrefabName} (netId {record.NetId}) arrived with " +
                             $"{registered.CopiedId:D}, which the ledger has already seen; it is now {registered.Entry.Id:D}");
        record.PersistentItemId = registered.Entry.Id;
        if (NetworkedItem.TryGet(record.NetId, out NetworkedItem item) && item != null)
            (item.GetComponent<ItemIdentity>() ?? item.gameObject.AddComponent<ItemIdentity>())
                .Value = registered.Entry.Id;
        netIdByPersistentId[registered.Entry.Id] = record.NetId;
    }

    /// <summary>
    /// A cold item's own id. Records written before the ledger have an id of their own; the item's is
    /// in its stored state, which withdrawal restores.
    /// </summary>
    internal static Guid ItemIdOf(StoredItemRecord cold)
    {
        if (cold.DetachedState?.Length > 0)
        {
            try
            {
                JObject state = JObject.Parse(Encoding.UTF8.GetString(cold.DetachedState));
                if (Guid.TryParse((string)state[ItemIdentityPatch.Key], out Guid id) && id != Guid.Empty)
                    return id;
            }
            catch (Exception exception)
            {
                Multiplayer.LogWarning($"Owned items: cold record {cold.PersistentItemId:D} has unreadable state: {exception.Message}");
            }
        }
        return cold.PersistentItemId;
    }

    private static BirthReason BirthFrom(ItemMoveReason reason) => reason switch
    {
        ItemMoveReason.Purchased => BirthReason.Purchased,
        ItemMoveReason.Granted => BirthReason.Granted,
        ItemMoveReason.Issued => BirthReason.Issued,
        ItemMoveReason.StarterKit => BirthReason.StarterKit,
        _ => BirthReason.Claimed
    };

    private static string ItemChangeCausesCallers() =>
        global::Multiplayer.Shared.Items.ChangeCauses.ItemChangeCauses.Callers(typeof(ServerLedger));

    // Vanilla's ShopRestocker restocks the shop when a bought item with this flag goes.
    private static bool IsShopStocked(ushort netId) =>
        NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null &&
        item.TryGetComponent(out ShopRestocker restocker) && restocker.restockOnItemDestroyed;
}
