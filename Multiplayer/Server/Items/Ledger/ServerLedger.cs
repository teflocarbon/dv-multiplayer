using DV.CabControls;
using DV.InventorySystem;
using DV.Shops;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Core.Items.SaveLoad;
using Multiplayer.Server.Items.Containers;
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
/// The server's ledger of owned items (docs/legacy/items/owned-items.md), in shadow (owned-items PR 1b):
/// it follows the item records where they commit (<see cref="ItemChangeLog"/>), is checked against
/// the stores once they have loaded, and is saved, but nothing reads it yet. What it disagrees with
/// is logged rather than acted on, so the logs show where it would change things.
/// </summary>
public static class ServerLedger
{
    internal const string SaveKey = "OwnedItemsV1";

    private static ItemLedger ledger = new();
    private static readonly Dictionary<Guid, ushort> netIdByPersistentId = new();
    private static bool loaded;

    public static ItemLedger Ledger => ledger;

    /// <summary>The saved ledger has been read and checked against the stores.</summary>
    public static bool Loaded => loaded;

    public static void Clear()
    {
        ledger = new ItemLedger();
        netIdByPersistentId.Clear();
        loaded = false;
    }

    public static void HostTick()
    {
        if (loaded || NetworkLifecycle.Instance?.IsHost() != true || SaveGameManager.Instance?.data == null ||
            !ItemSaveLoad.ImportAttempted || !LostAndFoundServer.ImportAttempted ||
            !ContainerServer.ImportAttempted)
            return;
        Load(SaveGameManager.Instance.data.GetJObject("Multiplayer"));
    }

    /// <summary>
    /// An id is for life and names one item. An item registering with an id another live item has
    /// (a copied save, a dupe, an object cloned with its identity) keeps the first where it is and
    /// gets a new id. Returns the id the item is to have.
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
            id = copy;
        }
        netIdByPersistentId[id] = item.NetId;
        return id;
    }

    /// <summary>Host: an accepted move of an item (<see cref="ItemChangeLog.RecordMove"/>).</summary>
    internal static void OnMove(LiveItems.LiveItem record, ChangeCause<ItemMoveReason> cause)
    {
        if (!loaded || record == null || record.PersistentItemId == Guid.Empty ||
            record.Owner == Guid.Empty)
            return;
        Guid id = record.PersistentItemId;
        Home where = WhereOf(record);
        if (!ledger.TryGet(id, out LedgerEntry entry) || !entry.IsLive)
        {
            Register(record, BirthFrom(cause.Reason), PlayerNumbers.Identity(cause.PlayerId), cause.Detail);
            return;
        }
        if (entry.Owner != record.Owner)
            Multiplayer.LogWarning($"Owned items: {entry} changed owner to {record.Owner:D} " +
                                   $"outside the ledger ({cause})");
        ledger.Move(id, where);
        if (HasRetrievalClaim(record) && entry.ClaimSlot >= 0 && entry.ClaimSlot != record.InventoryClaimSlot)
            ledger.SetClaimSlot(id, record.InventoryClaimSlot);
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
                ledger.Move(record.PersistentItemId, Home.LostAndFound);
                return;
            // OnDeposited moves it; leaving a player's interest is a client's view.
            case ItemRemovalReason.DepositedInContainer:
            case ItemRemovalReason.LeftInterest:
                return;
        }
        // Unloading the world or quitting destroys every item without ending any of them.
        if (UnloadWatcher.isUnloading || UnloadWatcher.isQuitting || !item.gameObject.scene.isLoaded)
            return;
        ledger.End(record.PersistentItemId, cause.Reason, detail: cause.Detail);
    }

    /// <summary>
    /// Host: the server made <paramref name="replacement"/> as vanilla used <paramref name="used"/> up
    /// (owned-items.md, "Lineage", PR 7): spent duct tape and its empty roll, an empty spool, a
    /// broken label. The replacement is the used item's owner's, born Replaced, and the used item's
    /// entry ends with replacedBy. Call it before the used item is destroyed. False when the used item
    /// isn't owned.
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
        made.Owner = usedEntry.Owner;
        if (replacement.Item?.InventorySpecs != null)
            replacement.Item.InventorySpecs.BelongsToPlayer = true;
        Registration registration = ledger.Replace(usedRecord.PersistentItemId, endReason, new BirthRequest
        {
            Id = made.PersistentItemId,
            Prefab = made.PrefabName,
            Where = WhereOf(made),
            Detail = detail ?? "replaced " + usedEntry.Prefab
        });
        return registration.Accepted;
    }

    /// <summary>Host: a deposit committed the item to a cold container under its own id.</summary>
    internal static void OnDeposited(Guid itemId, Guid containerId)
    {
        if (loaded)
            ledger.Move(itemId, Home.Container(containerId));
    }

    /// <summary>
    /// The next saves leave the ledger out, as a build without it would: the next load migrates. For
    /// the <c>ledger-save</c> scenario; it lasts until the game restarts.
    /// </summary>
    public static bool SaveWithoutLedger;

    public static void WriteSave(JObject multiplayerRoot)
    {
        if (multiplayerRoot != null && SaveWithoutLedger)
        {
            multiplayerRoot.Remove(SaveKey);
            return;
        }
        // Before the check against the stores, the saved ledger stays as it was loaded.
        if (multiplayerRoot == null || !loaded)
            return;
        multiplayerRoot[SaveKey] = LedgerSaveCodec.Write(ledger);
    }

    /// <summary>
    /// Reads the saved ledger and checks it against what the stores hold. A save without one is
    /// migrated: every owned item the stores hold is registered. In shadow the steps that would move
    /// items (dropping a copy, recovering another player's item) are logged, not carried out.
    /// </summary>
    internal static void Load(JObject multiplayerRoot)
    {
        loaded = true;
        JToken saved = multiplayerRoot?[SaveKey];
        List<LedgerEntry> entries = LedgerSaveCodec.Read(saved, out List<Guid> starterKits, out int unreadable);
        IReadOnlyList<LedgerEntry> refused = ledger.Restore(entries, starterKits);
        if (unreadable > 0 || refused.Count > 0)
            Multiplayer.LogWarning($"Owned items: skipped {unreadable} unreadable and {refused.Count} duplicate saved entries");

        IReadOnlyList<OwnedItemLoadStep> steps = LoadReconciler.Plan(ledger.All(), StoreHits());
        LoadReconciler.ApplyToLedger(ledger, steps);
        bool migrating = saved == null;
        if (migrating && multiplayerRoot?["Players"] is JObject players)
            foreach (KeyValuePair<string, JToken> player in players)
                if (Guid.TryParse(player.Key, out Guid id))
                    ledger.MarkStarterKit(id);

        Dictionary<OwnedItemLoadAction, int> counts = steps.GroupBy(step => step.Action)
            .ToDictionary(group => group.Key, group => group.Count());
        Multiplayer.Log($"Owned items: {(migrating ? "migrated a save without a ledger" : "loaded")}, " +
                        $"{ledger.Count} entries; " + string.Join(", ", counts.OrderBy(pair => pair.Key)
                            .Select(pair => $"{pair.Key} {pair.Value}")));
        foreach (OwnedItemLoadStep step in steps)
        {
            switch (step.Action)
            {
                case OwnedItemLoadAction.Keep:
                    break;
                case OwnedItemLoadAction.Register when migrating:
                    Multiplayer.LogDebug(() => "Owned items: " + step);
                    break;
                case OwnedItemLoadAction.DropCopy:
                case OwnedItemLoadAction.RecoverToLostAndFound:
                    Multiplayer.LogWarning("Owned items (shadow, not carried out): " + step);
                    break;
                default:
                    Multiplayer.LogWarning("Owned items: " + step);
                    break;
            }
        }
    }

    /// <summary>
    /// What disagrees between the ledger and the items: a live owned item the ledger doesn't have
    /// or has somewhere else, and vanilla storage holding an item where it shouldn't. Empty when
    /// all agree. For scenarios and the debug console.
    /// </summary>
    public static List<string> Audit()
    {
        List<string> problems = new();
        HashSet<Guid> pendingLost = PendingLostAndFound();
        foreach (LiveItems.LiveItem record in LiveItems.GetAll())
        {
            if (record.PersistentItemId == Guid.Empty || record.Owner == Guid.Empty ||
                pendingLost.Contains(record.PersistentItemId) ||
                record.Placement is ItemPlacementKind.Destroyed or ItemPlacementKind.Container)
                continue;
            if (!ledger.TryGet(record.PersistentItemId, out LedgerEntry entry) || !entry.IsLive)
                problems.Add($"{record.PrefabName} (netId {record.NetId}, {record.PersistentItemId:D}) is owned but not in the ledger");
            // The slot is left out: moving an item between inventory slots isn't a logged move.
            else if (!SamePlace(entry.Where, WhereOf(record)))
                problems.Add($"{entry}: the record says {WhereOf(record)}");
            else if (entry.Owner != record.Owner)
                problems.Add($"{entry}: the record's owner is {record.Owner:D}");
        }

        StorageController storage = StorageController.Instance;
        if (storage == null)
            return problems;
        foreach (ItemBase item in storage.StorageLostAndFound?.GetStorageItemList() ?? new List<ItemBase>())
            if (item != null && NetworkedItem.TryGetNetworkedItem(item, out NetworkedItem networked) &&
                LiveItems.TryGet(networked.NetId, out LiveItems.LiveItem record) &&
                record.Owner != Guid.Empty && record.Placement != ItemPlacementKind.LostAndFound &&
                !pendingLost.Contains(record.PersistentItemId))
                problems.Add($"{item.name} (netId {networked.NetId}, {record.PersistentItemId:D}) is in vanilla's Lost and Found, but its record says {record.Placement}");
        // A dedicated host's own inventory belongs to its parked body and stays empty (#261).
        if (NetworkLifecycle.Instance?.IsDedicatedHost == true)
            foreach (ItemBase item in storage.StorageInventory?.GetStorageItemList() ?? new List<ItemBase>())
                if (item != null)
                    problems.Add($"{item.name} is in the dedicated host's own inventory");
        return problems;
    }

    /// <summary>The ledger, one line per entry, for the debug console and scenarios.</summary>
    public static string Describe(Guid owner = default) => string.Join("\n",
        (owner == Guid.Empty ? ledger.All() : ledger.LiveOwnedBy(owner)).Select(entry => entry.ToString()));

    /// <summary>
    /// The one way into an owner's Lost and Found for an owned item by its id (owned-items.md, "What
    /// other systems read"): a purchase delivered far away, a deleted car's gadgets, and later
    /// retrieval. The item must be lying somewhere, not with a player. False, with a reason, when it
    /// can't go.
    /// </summary>
    public static bool SendToLostAndFound(Guid itemId, LostItemReason reason, string detail, out string refused)
    {
        refused = string.Empty;
        LiveItems.LiveItem record = LiveItems.GetAll()
            .FirstOrDefault(r => r.PersistentItemId == itemId && itemId != Guid.Empty);
        if (record == null || !NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null)
            refused = "unknown-item";
        else if (record.Owner == Guid.Empty)
            refused = "item-has-no-owner";
        else if (record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory)
            refused = "item-is-with-a-player";
        else if (record.Placement == ItemPlacementKind.LostAndFound)
            return true;
        else if (!LostAndFoundServer.Collect(item, reason, detail))
            refused = "collection-refused";
        return refused.Length == 0;
    }

    /// <summary>Where a record says its item is, as the ledger puts it.</summary>
    public static Home WhereOf(LiveItems.LiveItem record) => record.Placement switch
    {
        ItemPlacementKind.PlayerHand => Home.With(record.Holder, HoldKind.Hand),
        ItemPlacementKind.PlayerInventory => Home.With(record.Holder, HoldKind.Inventory,
            record.InventoryClaimSlot),
        ItemPlacementKind.TrainInterior => Home.World("car:" + record.WorldParentPersistentId),
        ItemPlacementKind.StaticParent => Home.World("static:" + record.WorldParentKey),
        ItemPlacementKind.Attached => Home.Attached("car:" + record.AttachedCarPersistentId),
        ItemPlacementKind.SnappedAttachment => Home.Snapped(record.WorldParentKey),
        ItemPlacementKind.Installed => Home.Installed("car:" + record.AttachedCarPersistentId, 0),
        ItemPlacementKind.LostAndFound => Home.LostAndFound,
        ItemPlacementKind.World => Home.World(),
        _ => Home.Missing
    };

    private static bool SamePlace(Home a, Home b) =>
        a.Place == b.Place && a.Holder == b.Holder && a.Hold == b.Hold && a.Anchor == b.Anchor && a.GadgetUid == b.GadgetUid;

    private static void Register(LiveItems.LiveItem record, BirthReason reason, Guid by, string detail)
    {
        Registration registered = ledger.Register(new BirthRequest
        {
            Id = record.PersistentItemId,
            Owner = record.Owner,
            Prefab = record.PrefabName,
            Where = WhereOf(record),
            Reason = reason,
            By = by,
            Detail = detail,
            Essential = HasRetrievalClaim(record),
            PreferredClaimSlot = record.InventoryClaimSlot,
            ShopStocked = IsShopStocked(record.NetId)
        });
        if (registered.Status != RegistrationStatus.Reminted)
            return;
        // An ended id came back: give the item the new one, as a duplicate gets.
        Multiplayer.LogError($"item.duplicate-persistent-id: {record.PrefabName} (netId {record.NetId}) arrived with " +
                             $"{registered.CopiedId:D}, which the ledger has already seen; it is now {registered.Entry.Id:D}");
        record.PersistentItemId = registered.Entry.Id;
        if (NetworkedItem.TryGet(record.NetId, out NetworkedItem item) && item != null)
            (item.GetComponent<ItemIdentity>() ?? item.gameObject.AddComponent<ItemIdentity>())
                .Value = registered.Entry.Id;
        netIdByPersistentId[registered.Entry.Id] = record.NetId;
    }

    // A Lost and Found entry whose owner hasn't joined is bound only when they do (until owned-items
    // PR 2a); until then its inert item has a record of its own that says World. The entry is the
    // item's place.
    private static HashSet<Guid> PendingLostAndFound() =>
        new(LostAndFoundServer.PendingEntries().Select(pending => pending.ItemId));

    private static IEnumerable<OwnedItemStoreHit> StoreHits()
    {
        HashSet<Guid> pendingLost = PendingLostAndFound();
        foreach (LiveItems.LiveItem record in LiveItems.GetAll())
        {
            if (record.PersistentItemId == Guid.Empty || pendingLost.Contains(record.PersistentItemId) ||
                record.Placement is ItemPlacementKind.Destroyed or ItemPlacementKind.Container)
                continue;
            Home where = WhereOf(record);
            OwnedItemStore? store = LoadReconciler.StoreFor(where);
            if (store.HasValue)
                yield return new OwnedItemStoreHit(record.PersistentItemId, store.Value, where,
                    record.Owner, record.PrefabName, IsShopStocked(record.NetId),
                    HasRetrievalClaim(record) ? record.InventoryClaimSlot : -1);
        }
        foreach (LostAndFoundServer.PendingEntry pending in LostAndFoundServer.PendingEntries())
            yield return new OwnedItemStoreHit(pending.ItemId, OwnedItemStore.LostAndFound, Home.LostAndFound,
                pending.Owner, pending.Prefab, claimSlot: pending.ClaimSlot);
        foreach (StoredItemRecord cold in ContainerServer.Graph.ExportSnapshot().Items)
        {
            Guid.TryParse(cold.PersistentOwnerIdentity, out Guid owner);
            yield return new OwnedItemStoreHit(ItemIdOf(cold), OwnedItemStore.Container,
                Home.Container(cold.ParentContainerId), owner, cold.PrefabName);
        }
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
        // Owned before the ledger saw it: restored, withdrawn, retrieved.
        ItemMoveReason.RestoredFromSave or
            ItemMoveReason.WithdrawnFromContainer or ItemMoveReason.RetrievedFromLostAndFound => BirthReason.Migrated,
        _ => BirthReason.Claimed
    };

    private static bool HasRetrievalClaim(LiveItems.LiveItem record) =>
        record.InventoryClaimSlot >= 0 &&
        (record.InventoryClaimFlags & (ItemInventoryClaimFlags.Reserved | ItemInventoryClaimFlags.Locked)) != 0;

    // Vanilla's ShopRestocker restocks the shop when a bought item with this flag goes.
    private static bool IsShopStocked(ushort netId) =>
        NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null &&
        item.TryGetComponent(out ShopRestocker restocker) && restocker.restockOnItemDestroyed;
}
