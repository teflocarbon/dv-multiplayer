using DV.CabControls;
using DV.InventorySystem;
using DV.Shops;
using Multiplayer.Core.Containers;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Multiplayer.Server.Items;

/// <summary>
/// The server's ledger of owned items (docs/design/owned-items.md), in shadow (owned-items PR 1b):
/// it follows the item records where they commit (<see cref="ItemChangeLog"/>), is checked against
/// the stores once they have loaded, and is saved, but nothing reads it yet. What it disagrees with
/// is logged rather than acted on, so the logs show where it would change things.
/// </summary>
public static class OwnedItemsServer
{
    internal const string SaveKey = "OwnedItemsV1";

    private static OwnedItemLedger ledger = new();
    private static readonly Dictionary<Guid, ushort> netIdByPersistentId = new();
    private static bool loaded;

    public static OwnedItemLedger Ledger => ledger;

    /// <summary>The saved ledger has been read and checked against the stores.</summary>
    public static bool Loaded => loaded;

    public static void Clear()
    {
        ledger = new OwnedItemLedger();
        netIdByPersistentId.Clear();
        loaded = false;
    }

    public static void HostTick()
    {
        if (loaded || NetworkLifecycle.Instance?.IsHost() != true || SaveGameManager.Instance?.data == null ||
            !WorldItemPersistenceManager.ImportAttempted || !NetworkedLostAndFoundManager.ImportAttempted ||
            !NetworkedColdContainerManager.ImportAttempted)
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
            AuthoritativeItemRegistry.TryGet(other, out AuthoritativeItemRegistry.Record original) &&
            original.PersistentItemId == id && NetworkedItem.TryGet(other, out NetworkedItem originalItem) &&
            originalItem != null)
        {
            Guid copy = Guid.NewGuid();
            Multiplayer.LogError($"item.duplicate-persistent-id: {item.name} (netId {item.NetId}) arrived with id {id:D}, " +
                                 $"which {originalItem.name} (netId {other}, {original.Placement}) has; " +
                                 $"the first keeps it and the copy is now {copy:D}");
            PersistentWorldItemIdentity identity = item.GetComponent<PersistentWorldItemIdentity>() ??
                                                   item.gameObject.AddComponent<PersistentWorldItemIdentity>();
            identity.Value = copy;
            id = copy;
        }
        netIdByPersistentId[id] = item.NetId;
        return id;
    }

    /// <summary>Host: an accepted move of an item (<see cref="ItemChangeLog.RecordMove"/>).</summary>
    internal static void OnMove(AuthoritativeItemRegistry.Record record, ChangeCause<ItemMoveReason> cause)
    {
        if (!loaded || record == null || record.PersistentItemId == Guid.Empty ||
            record.PersistentOwnerIdentity == Guid.Empty)
            return;
        Guid id = record.PersistentItemId;
        OwnedItemWhere where = WhereOf(record);
        if (!ledger.TryGet(id, out OwnedItemEntry entry) || !entry.IsLive)
        {
            Register(record, BirthFrom(cause.Reason), Identity(cause.PlayerId), cause.Detail);
            return;
        }
        if (entry.Owner != record.PersistentOwnerIdentity)
            Multiplayer.LogWarning($"Owned items: {entry} changed owner to {record.PersistentOwnerIdentity:D} " +
                                   $"outside the ledger ({cause})");
        ledger.Move(id, where);
        if (HasRetrievalClaim(record) && entry.ClaimSlot >= 0 && entry.ClaimSlot != record.InventoryClaimSlot)
            ledger.SetClaimSlot(id, record.InventoryClaimSlot);
    }

    /// <summary>Host: an item is going away (<see cref="ItemChangeLog.RecordRemoval"/>).</summary>
    internal static void OnRemoved(NetworkedItem item, ChangeCause<ItemRemovalReason> cause)
    {
        if (!loaded || item == null || !AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record) ||
            !ledger.IsLive(record.PersistentItemId))
            return;
        switch (cause.Reason)
        {
            case ItemRemovalReason.CollectedToLostAndFound:
                ledger.Move(record.PersistentItemId, OwnedItemWhere.LostAndFound);
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
            !AuthoritativeItemRegistry.TryGet(used.NetId, out AuthoritativeItemRegistry.Record usedRecord) ||
            !ledger.TryGet(usedRecord.PersistentItemId, out OwnedItemEntry usedEntry) || !usedEntry.IsLive)
            return false;
        AuthoritativeItemRegistry.Record made = AuthoritativeItemRegistry.Ensure(replacement);
        if (made == null || made.PersistentItemId == Guid.Empty)
            return false;
        made.PersistentOwnerIdentity = usedEntry.Owner;
        made.PersistentOwnerPlayerId = OwnerNumber(usedEntry.Owner);
        if (replacement.Item?.InventorySpecs != null)
            replacement.Item.InventorySpecs.BelongsToPlayer = true;
        OwnedItemRegistration registration = ledger.Replace(usedRecord.PersistentItemId, endReason, new OwnedItemBirthRequest
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
            ledger.Move(itemId, OwnedItemWhere.Container(containerId));
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
        multiplayerRoot[SaveKey] = OwnedItemsSaveCodec.Write(ledger);
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
        List<OwnedItemEntry> entries = OwnedItemsSaveCodec.Read(saved, out List<Guid> starterKits, out int unreadable);
        IReadOnlyList<OwnedItemEntry> refused = ledger.Restore(entries, starterKits);
        if (unreadable > 0 || refused.Count > 0)
            Multiplayer.LogWarning($"Owned items: skipped {unreadable} unreadable and {refused.Count} duplicate saved entries");

        IReadOnlyList<OwnedItemLoadStep> steps = OwnedItemReconciler.Plan(ledger.All(), StoreHits());
        OwnedItemReconciler.ApplyToLedger(ledger, steps);
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
        foreach (AuthoritativeItemRegistry.Record record in AuthoritativeItemRegistry.GetAll())
        {
            if (record.PersistentItemId == Guid.Empty || record.PersistentOwnerIdentity == Guid.Empty ||
                pendingLost.Contains(record.PersistentItemId) ||
                record.Placement is ItemPlacementKind.Destroyed or ItemPlacementKind.Container)
                continue;
            if (!ledger.TryGet(record.PersistentItemId, out OwnedItemEntry entry) || !entry.IsLive)
                problems.Add($"{record.PrefabName} (netId {record.NetId}, {record.PersistentItemId:D}) is owned but not in the ledger");
            // The slot is left out: moving an item between inventory slots isn't a logged move.
            else if (!SamePlace(entry.Where, WhereOf(record)))
                problems.Add($"{entry}: the record says {WhereOf(record)}");
            else if (entry.Owner != record.PersistentOwnerIdentity)
                problems.Add($"{entry}: the record's owner is {record.PersistentOwnerIdentity:D}");
        }

        StorageController storage = StorageController.Instance;
        if (storage == null)
            return problems;
        foreach (ItemBase item in storage.StorageLostAndFound?.GetStorageItemList() ?? new List<ItemBase>())
            if (item != null && NetworkedItem.TryGetNetworkedItem(item, out NetworkedItem networked) &&
                AuthoritativeItemRegistry.TryGet(networked.NetId, out AuthoritativeItemRegistry.Record record) &&
                record.PersistentOwnerIdentity != Guid.Empty && record.Placement != ItemPlacementKind.LostAndFound &&
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
        AuthoritativeItemRegistry.Record record = AuthoritativeItemRegistry.GetAll()
            .FirstOrDefault(r => r.PersistentItemId == itemId && itemId != Guid.Empty);
        if (record == null || !NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null)
            refused = "unknown-item";
        else if (record.PersistentOwnerPlayerId == 0)
            refused = "item-has-no-owner";
        else if (record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory)
            refused = "item-is-with-a-player";
        else if (record.Placement == ItemPlacementKind.LostAndFound)
            return true;
        else if (!NetworkedLostAndFoundManager.Collect(item, reason, detail))
            refused = "collection-refused";
        return refused.Length == 0;
    }

    /// <summary>
    /// The owner number for an owner's identity: their session's player number while they are
    /// connected, <see cref="ItemTransitionCommand.AbsentOwnerId"/> while they aren't, 0 for no owner.
    /// </summary>
    public static byte OwnerNumber(Guid owner)
    {
        if (owner == Guid.Empty)
            return 0;
        ServerPlayer player = NetworkServer.Instance?.ServerPlayers.FirstOrDefault(p => p.Guid == owner);
        return player != null ? player.PlayerId : ItemTransitionCommand.AbsentOwnerId;
    }

    /// <summary>Where a record says its item is, as the ledger puts it.</summary>
    public static OwnedItemWhere WhereOf(AuthoritativeItemRegistry.Record record) => record.Placement switch
    {
        ItemPlacementKind.PlayerHand => OwnedItemWhere.With(Holder(record), OwnedItemHold.Hand),
        ItemPlacementKind.PlayerInventory => OwnedItemWhere.With(Holder(record), OwnedItemHold.Inventory,
            record.InventoryClaimSlot),
        ItemPlacementKind.TrainInterior => OwnedItemWhere.World("car:" + record.WorldParentPersistentId),
        ItemPlacementKind.StaticParent => OwnedItemWhere.World("static:" + record.WorldParentKey),
        ItemPlacementKind.Attached => OwnedItemWhere.Attached("car:" + record.AttachedCarPersistentId),
        ItemPlacementKind.SnappedAttachment => OwnedItemWhere.Snapped(record.WorldParentKey),
        ItemPlacementKind.Installed => OwnedItemWhere.Installed("car:" + record.AttachedCarPersistentId, 0),
        ItemPlacementKind.LostAndFound => OwnedItemWhere.LostAndFound,
        ItemPlacementKind.World => OwnedItemWhere.World(),
        _ => OwnedItemWhere.Missing
    };

    private static bool SamePlace(OwnedItemWhere a, OwnedItemWhere b) =>
        a.Place == b.Place && a.Holder == b.Holder && a.Hold == b.Hold && a.Anchor == b.Anchor && a.GadgetUid == b.GadgetUid;

    private static void Register(AuthoritativeItemRegistry.Record record, OwnedItemBirth reason, Guid by, string detail)
    {
        OwnedItemRegistration registered = ledger.Register(new OwnedItemBirthRequest
        {
            Id = record.PersistentItemId,
            Owner = record.PersistentOwnerIdentity,
            Prefab = record.PrefabName,
            Where = WhereOf(record),
            Reason = reason,
            By = by,
            Detail = detail,
            Essential = HasRetrievalClaim(record),
            PreferredClaimSlot = record.InventoryClaimSlot,
            ShopStocked = IsShopStocked(record.NetId)
        });
        if (registered.Status != OwnedItemRegistrationStatus.Reminted)
            return;
        // An ended id came back: give the item the new one, as a duplicate gets.
        Multiplayer.LogError($"item.duplicate-persistent-id: {record.PrefabName} (netId {record.NetId}) arrived with " +
                             $"{registered.CopiedId:D}, which the ledger has already seen; it is now {registered.Entry.Id:D}");
        record.PersistentItemId = registered.Entry.Id;
        if (NetworkedItem.TryGet(record.NetId, out NetworkedItem item) && item != null)
            (item.GetComponent<PersistentWorldItemIdentity>() ?? item.gameObject.AddComponent<PersistentWorldItemIdentity>())
                .Value = registered.Entry.Id;
        netIdByPersistentId[registered.Entry.Id] = record.NetId;
    }

    // A Lost and Found entry whose owner hasn't joined is bound only when they do (until owned-items
    // PR 2a); until then its inert item has a record of its own that says World. The entry is the
    // item's place.
    private static HashSet<Guid> PendingLostAndFound() =>
        new(NetworkedLostAndFoundManager.PendingEntries().Select(pending => pending.ItemId));

    private static IEnumerable<OwnedItemStoreHit> StoreHits()
    {
        HashSet<Guid> pendingLost = PendingLostAndFound();
        foreach (AuthoritativeItemRegistry.Record record in AuthoritativeItemRegistry.GetAll())
        {
            if (record.PersistentItemId == Guid.Empty || pendingLost.Contains(record.PersistentItemId) ||
                record.Placement is ItemPlacementKind.Destroyed or ItemPlacementKind.Container)
                continue;
            OwnedItemWhere where = WhereOf(record);
            OwnedItemStore? store = OwnedItemReconciler.StoreFor(where);
            if (store.HasValue)
                yield return new OwnedItemStoreHit(record.PersistentItemId, store.Value, where,
                    record.PersistentOwnerIdentity, record.PrefabName, IsShopStocked(record.NetId),
                    HasRetrievalClaim(record) ? record.InventoryClaimSlot : -1);
        }
        foreach (NetworkedLostAndFoundManager.PendingEntry pending in NetworkedLostAndFoundManager.PendingEntries())
            yield return new OwnedItemStoreHit(pending.ItemId, OwnedItemStore.LostAndFound, OwnedItemWhere.LostAndFound,
                pending.Owner, pending.Prefab, claimSlot: pending.ClaimSlot);
        foreach (ColdStoredItemRecord cold in NetworkedColdContainerManager.Graph.ExportSnapshot().Items)
        {
            Guid.TryParse(cold.PersistentOwnerIdentity, out Guid owner);
            yield return new OwnedItemStoreHit(ItemIdOf(cold), OwnedItemStore.Container,
                OwnedItemWhere.Container(cold.ParentContainerId), owner, cold.PrefabName);
        }
    }

    /// <summary>
    /// A cold item's own id. Records written before the ledger have an id of their own; the item's is
    /// in its stored state, which withdrawal restores.
    /// </summary>
    internal static Guid ItemIdOf(ColdStoredItemRecord cold)
    {
        if (cold.DetachedState?.Length > 0)
        {
            try
            {
                JObject state = JObject.Parse(Encoding.UTF8.GetString(cold.DetachedState));
                if (Guid.TryParse((string)state[PersistentWorldItemIdentityPatch.Key], out Guid id) && id != Guid.Empty)
                    return id;
            }
            catch (Exception exception)
            {
                Multiplayer.LogWarning($"Owned items: cold record {cold.PersistentItemId:D} has unreadable state: {exception.Message}");
            }
        }
        return cold.PersistentItemId;
    }

    private static OwnedItemBirth BirthFrom(ItemMoveReason reason) => reason switch
    {
        ItemMoveReason.Purchased => OwnedItemBirth.Purchased,
        ItemMoveReason.Granted => OwnedItemBirth.Granted,
        ItemMoveReason.Issued => OwnedItemBirth.Issued,
        ItemMoveReason.StarterKit => OwnedItemBirth.StarterKit,
        // Nothing is adopted any more (owned-items.md, PR 5); the value stays in old logs.
        ItemMoveReason.Adopted => OwnedItemBirth.Issued,
        // Owned before the ledger saw it: restored, withdrawn, retrieved.
        ItemMoveReason.RestoredFromSave or ItemMoveReason.ReturnedToRejoiningOwner or
            ItemMoveReason.WithdrawnFromContainer or ItemMoveReason.RetrievedFromLostAndFound => OwnedItemBirth.Migrated,
        _ => OwnedItemBirth.Claimed
    };

    private static Guid Holder(AuthoritativeItemRegistry.Record record) =>
        record.PlacementPlayerIdentity != Guid.Empty ? record.PlacementPlayerIdentity : Identity(record.PlacementPlayerId);

    private static Guid Identity(byte playerId) =>
        playerId != 0 && NetworkServer.Instance != null && NetworkServer.Instance.TryGetServerPlayer(playerId, out ServerPlayer player)
            ? player.Guid
            : Guid.Empty;

    private static bool HasRetrievalClaim(AuthoritativeItemRegistry.Record record) =>
        record.InventoryClaimSlot >= 0 &&
        (record.InventoryClaimFlags & (ItemInventoryClaimFlags.Reserved | ItemInventoryClaimFlags.Locked)) != 0;

    // Vanilla's ShopRestocker restocks the shop when a bought item with this flag goes.
    private static bool IsShopStocked(ushort netId) =>
        NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null &&
        item.TryGetComponent(out ShopRestocker restocker) && restocker.restockOnItemDestroyed;
}
