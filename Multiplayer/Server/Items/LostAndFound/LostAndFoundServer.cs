using DV.CabControls;
using DV.InventorySystem;
using DV.JObjectExtstensions;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.Access;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Inventory;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.Access;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Jobs;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.ChangeCauses;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Debugging.Protocol;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items.LostAndFound;

/// <summary>
/// Host-authoritative virtual Lost and Found. An entry is data on the server (OI-COLD-08): its object
/// goes once it can, and its state is a row here, made again only for a retrieval or a recall.
/// </summary>
public static class LostAndFoundServer
{
    private const uint ScanIntervalTicks = 24;
    private static uint lastScanTick; // the tick can advance by more than one a frame
    private static readonly LostItemRegistry registry = new();
    // The entries kept as data, by item id: each one's state (OI-COLD-08).
    private static readonly Dictionary<Guid, SavedItemRow> rows = new();
    // The owners sent the silhouette of an essential of theirs kept as data, by its NetId.
    private static readonly Dictionary<ushort, HashSet<ServerPlayer>> dataSentTo = new();
    private static readonly Dictionary<ushort, float> eligibleSince = new();
    private static readonly Dictionary<ushort, Dictionary<string, object>> lastEvaluations = new();
    private static uint generation;

    public static uint Generation => generation;
    public static bool Contains(ushort itemNetId) => registry.TryGet(itemNetId, out _);
    /// <summary>The id of an item's entry: its own persistent id (ledger.md, "The entry"), or empty for none.</summary>
    public static Guid EntryId(ushort itemNetId) =>
        registry.TryGet(itemNetId, out LostItemRecord record) ? record.PersistentItemId : Guid.Empty;
    public static int Count => registry.Count;

    /// <summary>Whose entry an item's is: its owner, or the player a booklet was issued to (OI-LF-17).</summary>
    public static Guid KeeperOf(ushort itemNetId) =>
        registry.TryGet(itemNetId, out LostItemRecord lost) ? OwnerOf(lost) : Guid.Empty;

    /// <summary>An entry kept as data: its row (OI-COLD-08).</summary>
    internal static bool TryGetDataRow(Guid itemId, out SavedItemRow row) => rows.TryGetValue(itemId, out row);

    internal static IEnumerable<SavedItemRow> DataRows() => rows.Values;

    /// <summary>Whether an item is in Lost and Found as data, with no object.</summary>
    internal static bool IsData(ushort itemNetId) =>
        registry.TryGet(itemNetId, out LostItemRecord lost) && rows.ContainsKey(lost.PersistentItemId) &&
        !(NetworkedItem.TryGet(itemNetId, out NetworkedItem item) && item != null);

    /// <summary>
    /// The object of an item in Lost and Found: the one there is, or made again from its row under
    /// its NetId, inert in Lost and Found as it went (OI-COLD-08). Null when there is neither. One
    /// made for a request that is then refused goes back to data at the next scan.
    /// </summary>
    internal static NetworkedItem ObjectOf(ushort itemNetId)
    {
        if (NetworkedItem.TryGet(itemNetId, out NetworkedItem item) && item != null)
            return item;
        if (!registry.TryGet(itemNetId, out LostItemRecord lost) ||
            !rows.TryGetValue(lost.PersistentItemId, out SavedItemRow row) ||
            !LiveItems.TryGet(itemNetId, out LiveItems.LiveItem record))
            return null;
        item = PlayerItems.FromLostData(record, row);
        if (item == null)
            return null;
        rows.Remove(lost.PersistentItemId);
        dataSentTo.Remove(itemNetId);
        ProjectToInertStorage(item);
        return item;
    }

    // An entry's object goes to data once it can (OI-COLD-08): its state loaded, its ammo able to go with it.
    private static void ToData(NetworkedItem item, LostItemRecord lost)
    {
        if (!ServerLedger.Loaded || !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record) ||
            record.Placement != ItemPlacementKind.LostAndFound)
            return;
        SavedItemRow row = PlayerItems.ToLostData(record, item);
        if (row != null)
            rows[lost.PersistentItemId] = row;
    }

    /// <summary>
    /// An owner's essential kept as data here keeps their red silhouette, their recall: they are sent
    /// it once, a Create and its tombstone made from its record, as the scan sends an object's.
    /// </summary>
    internal static void SendDataEssentials(ServerPlayer player)
    {
        if (rows.Count == 0 || player == null || player.Peer == NetworkServer.Instance.SelfPeer)
            return;
        foreach (LostItemRecord lost in registry.GetForOwner(player.Guid.ToString("D")))
        {
            if (!rows.ContainsKey(lost.PersistentItemId) || !LiveItems.TryGet(lost.NetId, out LiveItems.LiveItem record) ||
                record.InventoryClaimSlot < 0 ||
                (record.InventoryClaimFlags & (ItemInventoryClaimFlags.Reserved | ItemInventoryClaimFlags.Locked)) == 0 ||
                ItemSaveLoad.FindSpec(record.PrefabName)?.IsEssential != true)
                continue;
            if (!dataSentTo.TryGetValue(lost.NetId, out HashSet<ServerPlayer> sent))
                dataSentTo[lost.NetId] = sent = new HashSet<ServerPlayer>();
            if (!sent.Add(player))
                continue;
            ItemUpdate create = new()
            {
                UpdateType = ItemUpdate.ItemUpdateType.Create,
                ItemNetId = lost.NetId,
                PrefabName = record.PrefabName,
                ItemState = ItemState.Dropped,
                BelongsToPlayer = true
            };
            LiveItems.WriteToSnapshot(record, create, record.LastReason);
            ItemUpdate tombstone = new()
            {
                UpdateType = ItemUpdate.ItemUpdateType.Destroy,
                ItemNetId = lost.NetId,
                PrefabName = record.PrefabName
            };
            LiveItems.WriteToSnapshot(record, tombstone, ItemTransitionReason.LostAndFoundCollection);
            tombstone.AuthorityRevision = lost.Revision;
            tombstone.RemovalCause = CollectionCause(lost.Reason, OwnerOf(lost), null);
            NetworkServer.Instance.SendItemUpdate(create, player);
            NetworkServer.Instance.SendItemUpdate(tombstone, player);
        }
    }

    public static bool TryCreateCollectionProjection(NetworkedItem item,
        out ItemUpdate snapshot)
    {
        snapshot = null;
        if (item == null || !registry.TryGet(item.NetId, out LostItemRecord lost) ||
            !LiveItems.TryGet(item.NetId,
                out LiveItems.LiveItem authority) ||
            authority.Placement != ItemPlacementKind.LostAndFound)
            return false;

        snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Destroy);
        if (snapshot == null)
            return false;
        LiveItems.WriteToSnapshot(authority, snapshot,
            ItemTransitionReason.LostAndFoundCollection);
        snapshot.AuthorityRevision = lost.Revision;
        snapshot.RemovalCause = CollectionCause(lost.Reason, OwnerOf(lost), null);
        return true;
    }

    public static void HostTick(uint tick)
    {
        if (!NetworkLifecycle.Instance.IsHost() || unchecked(tick - lastScanTick) < ScanIntervalTicks ||
            !ServerLedger.Loaded)
            return;
        lastScanTick = tick;
        ReconcileCanonicalPlacements();
        float now = Time.realtimeSinceStartup;
        bool capturePolicyDiagnostics = DebugOverlayController.LostAndFoundDiagnosticsVisible;
        if (!capturePolicyDiagnostics && lastEvaluations.Count > 0)
            lastEvaluations.Clear();
        HashSet<ushort> seen = new();
        foreach (NetworkedItem item in NetworkedItem.GetAll().Where(value => value != null).Distinct().ToArray())
        {
            if (item.NetId == 0 || !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem authority))
                continue;
            seen.Add(item.NetId);
            // A binned item is going, its binner's choice (OI-VAN-02), not lost.
            if (VanillaStorage.Binning.IsBinned(item.NetId))
                continue;
            LostItemCandidate candidate = BuildCandidate(item, authority, now);
            LostItemDecision initial = Evaluate(candidate);
            if (initial.Kind is LostItemDecisionKind.Ineligible or LostItemDecisionKind.Protected)
            {
                if (capturePolicyDiagnostics)
                    RecordEvaluation(item, authority, initial, candidate);
                eligibleSince.Remove(item.NetId);
                continue;
            }
            if (!eligibleSince.TryGetValue(item.NetId, out float since))
                eligibleSince[item.NetId] = since = now;
            candidate.EligibleDurationSeconds = now - since;
            LostItemDecision decision = Evaluate(candidate);
            if (capturePolicyDiagnostics)
                RecordEvaluation(item, authority, decision, candidate);
            TraceDecision(item, authority, decision, candidate);
            if (decision.ShouldCollect)
                Collect(item, LostItemReason.OwnerDistance);
        }
        foreach (ushort id in eligibleSince.Keys.Where(id => !seen.Contains(id)).ToArray())
            eligibleSince.Remove(id);
        foreach (ushort id in lastEvaluations.Keys.Where(id => !seen.Contains(id)).ToArray())
            lastEvaluations.Remove(id);
    }

    /// <summary>
    /// A player's Lost and Found as list rows (docs/design/items/wire.md, "One list"), oldest
    /// first: each named by its item's handle, with the reason it's there and its claim slot.
    /// </summary>
    public static ItemRow[] RowsFor(ServerPlayer player)
    {
        ReconcileCanonicalPlacements();
        return player == null ? Array.Empty<ItemRow>() :
            registry.GetForOwner(player.Guid.ToString("D")).OrderBy(record => record.LostUtcTicks)
                .Select(ToRow).ToArray();
    }

    /// <summary>
    /// Collects a Lost and Found row into <paramref name="player"/>'s inventory: the entry of the
    /// item <paramref name="itemId"/>, at the revision its row showed. The slots are the server's
    /// (R3), not the client's: an essential goes back to its claim slot, where its silhouette is,
    /// unless another item has it.
    /// </summary>
    public static bool TryCollect(ServerPlayer player, Guid itemId, uint expectedRevision,
        int requestedSlot, out ItemUpdate snapshot, out string rejectionReason)
    {
        snapshot = null;
        // No request is served before the load has run (OI-PER-23).
        if (!ServerLedger.Loaded)
        {
            rejectionReason = "items-loading";
            return false;
        }
        if (!registry.TryGetByPersistentId(itemId, out LostItemRecord lost))
        {
            rejectionReason = "unknown-lost-item";
            TraceRecallRejection(0, rejectionReason);
            return false;
        }
        int existingSlot = lost.InventoryClaimSlot >= 0 &&
            Array.IndexOf(ServerLedger.TakenSlots(lost.PersistentItemId, player.Guid), lost.InventoryClaimSlot) < 0
            ? lost.InventoryClaimSlot : -1;
        return TryRetrieve(player, lost.Handle, expectedRevision,
            requestedSlot >= 0 ? requestedSlot : lost.InventoryClaimSlot, existingSlot, out snapshot, out rejectionReason);
    }

    private static bool TryRetrieve(ServerPlayer player, uint lostHandle, uint expectedRevision,
        int requestedSlot, int existingItemSlot, out ItemUpdate snapshot, out string rejectionReason)
    {
        snapshot = null;
        rejectionReason = string.Empty;
        global::Multiplayer.Multiplayer.Log($"[LostAndFound Recall] Manager retrieval started: " +
            $"player=P{player?.PlayerId ?? 0}, handle={lostHandle}, expectedRevision={expectedRevision}, " +
            $"requestedSlot={requestedSlot}, existingSlot={existingItemSlot}, " +
            $"registryContains={registry.TryGetByHandle(lostHandle, out _)}");
        if (player == null || !registry.TryGetByHandle(lostHandle, out LostItemRecord lost))
        {
            rejectionReason = "unknown-lost-item";
            TraceRecallRejection(0, rejectionReason, lostHandle);
            return false;
        }
        ushort itemNetId = lost.NetId;
        LostItemRetrievalPlan plan = LostItemRetrievalPlanner.Plan(lost, player.Guid.ToString("D"),
            expectedRevision, requestedSlot, existingItemSlot, InventorySlots.Capacity,
            ServerLedger.TakenSlots(lost.PersistentItemId, player.Guid));
        if (!plan.Accepted)
        {
            rejectionReason = plan.Reason;
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        global::Multiplayer.Multiplayer.Log($"[LostAndFound Recall] Retrieval plan accepted: " +
            $"netId={itemNetId}, targetSlot={plan.TargetSlot}, storedRevision={lost.Revision}, " +
            $"owner={lost.OwnerIdentity}");
        NetworkedItem item = ObjectOf(itemNetId);
        if (item == null && rows.ContainsKey(lost.PersistentItemId))
        {
            // Kept as data and couldn't be made again (its prefab gone): the entry stays, for a later try.
            rejectionReason = "lost-item-not-made";
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        if (item == null)
        {
            // Nothing is left to retrieve: drop the entry so the owner's list stops offering it.
            RemoveStaleEntry(itemNetId, "canonical-item-destroyed");
            rejectionReason = "missing-canonical-unity-item";
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        if (!LiveItems.TryGet(itemNetId, out LiveItems.LiveItem authority) ||
            authority.Placement != ItemPlacementKind.LostAndFound)
        {
            RemoveStaleEntry(itemNetId, "canonical-item-no-longer-in-lost-and-found");
            rejectionReason = "item-not-in-lost-and-found";
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        // Asked as a recall of a live item is (OI-ACC-05).
        if (!ItemAccessServer.Allows(player, ItemAccessAction.Recall, item, authority, out rejectionReason))
        {
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        if (!LiveItems.TryRetrieveFromLostAndFound(item, player,
                expectedRevision, plan.TargetSlot, out snapshot, out rejectionReason))
        {
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        global::Multiplayer.Multiplayer.Log($"[LostAndFound Recall] Authority transition accepted: " +
            $"netId={itemNetId}, newRevision={snapshot?.AuthorityRevision ?? 0}, " +
            $"state={snapshot?.ItemState.ToString() ?? "null"}, targetSlot={plan.TargetSlot}");
        LostItemRegistryResult removed = registry.RemoveForRetrieval(itemNetId, player.Guid.ToString("D"),
            expectedRevision, out LostItemRecord removedRecord);
        if (removed != LostItemRegistryResult.Removed)
        {
            LiveItems.RestoreLostRecord(item, player.Guid,
                expectedRevision, lost.PrefabName,
                lost.InventoryClaimSlot, (ItemInventoryClaimFlags)lost.InventoryClaimFlags);
            rejectionReason = "lost-item-registry-changed";
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        try
        {
            RestoreFromProjection(item);
            item.ApplyServerCanonicalSnapshot(snapshot);
        }
        catch (Exception exception)
        {
            LogRestore(registry.Restore(removedRecord), removedRecord.PersistentItemId, "retrieval rollback");
            LiveItems.RestoreLostRecord(item, player.Guid,
                expectedRevision, lost.PrefabName,
                lost.InventoryClaimSlot, (ItemInventoryClaimFlags)lost.InventoryClaimFlags);
            try { ProjectToInertStorage(item); }
            catch (Exception rollbackException)
            {
                Multiplayer.LogException("Lost and Found retrieval projection rollback failed", rollbackException);
            }
            rejectionReason = "unity-retrieval-application-failed";
            Multiplayer.LogException("Lost and Found retrieval failed", exception);
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        // The transition indexed the item while its entry still held it, which skips it: index it
        // now, or a player who joins later (its owner, rejoining) is never sent it.
        ServerItemManager.Instance?.RegisterHostWorldItem(item);
        generation++;
        if (NetworkServer.Instance.TryGetServerPlayer(player.PlayerId,
                out ServerPlayer owner))
            NetworkServer.Instance.SendLostAndFoundList(owner);
        Publish("item.lost-and-found-retrieved", item, new()
        {
            ["ownerPlayerId"] = player.PlayerId,
            ["targetSlot"] = plan.TargetSlot,
            ["generation"] = generation
        });
        global::Multiplayer.Multiplayer.Log($"[LostAndFound Recall] Manager retrieval completed: " +
            $"netId={itemNetId}, revision={snapshot.AuthorityRevision}, targetSlot={plan.TargetSlot}, " +
            $"active={item.gameObject.activeSelf}, registryContains={registry.TryGet(itemNetId, out _)}");
        return true;
    }

    /// <summary>
    /// Handles the base game's reserved-slot Return button for an item that has already entered
    /// the virtual Lost and Found. The supplied slot is the existing immutable silhouette slot.
    /// </summary>
    public static bool TryRetrieveForRecall(ServerPlayer player, NetworkedItem item,
        uint expectedRevision, int existingItemSlot, out ItemUpdate snapshot,
        out string rejectionReason)
    {
        if (existingItemSlot < 0)
        {
            snapshot = null;
            rejectionReason = "recall-claim-slot-missing";
            TraceRecallRejection(item?.NetId ?? 0, rejectionReason);
            return false;
        }
        if (item == null || !registry.TryGet(item.NetId, out LostItemRecord lost))
        {
            snapshot = null;
            rejectionReason = "unknown-lost-item";
            TraceRecallRejection(item?.NetId ?? 0, rejectionReason);
            return false;
        }
        uint authoritativeRevision = lost.Revision;
        global::Multiplayer.Multiplayer.Log($"[LostAndFound Recall] Converting star recall to retrieval: " +
            $"player=P{player?.PlayerId ?? 0}, netId={item.NetId}, projectionRevision={expectedRevision}, " +
            $"registryRevision={authoritativeRevision}, " +
            $"silhouetteSlot={existingItemSlot}");
        // Collection advances canonical authority while deliberately leaving a dropped inventory
        // silhouette behind. Older projections can therefore report the pre-collection revision.
        // The star action identifies that same canonical registry entry, so validate against the
        // host-owned registry revision rather than the inert Unity projection's cached revision.
        return TryRetrieve(player, lost.Handle, authoritativeRevision, existingItemSlot,
            existingItemSlot, out snapshot, out rejectionReason);
    }

    /// <param name="detail">What the reason leaves out, for the logged cause (the deleted car).</param>
    public static bool Collect(NetworkedItem item, LostItemReason reason, string detail = null)
    {
        if (!NetworkLifecycle.Instance.IsHost() || item == null || item.NetId == 0)
            return false;
        if (registry.TryGet(item.NetId, out _))
            return false;
        // An owned item, by the ledger: vanilla's BelongsToPlayer flips in the dumpster, the shop and
        // a withdrawal, so nothing decides on it (docs/design/item-refactor.md, "Every vanilla path").
        // A booklet, which nobody owns, is kept for the player it was issued to (OI-LF-17).
        Guid keeper = LiveItems.TryGet(item.NetId, out LiveItems.LiveItem owned) ? LiveItems.KeeperOf(owned) : Guid.Empty;
        if (keeper == Guid.Empty)
        {
            Publish("item.lost-and-found-collection-rejected", item,
                new() { ["rejectionReason"] = "item-has-no-owner" }, DebugSeverity.Warning);
            return false;
        }
        if (IsInLocalInventory(item))
        {
            Publish("item.lost-and-found-collection-rejected", item,
                new() { ["rejectionReason"] = "item-still-in-local-inventory" }, DebugSeverity.Warning);
            return false;
        }
        if (!LiveItems.TryGet(item.NetId, out LiveItems.LiveItem previousAuthority))
            return false;
        ItemPlacementKind previousPlacement = previousAuthority.Placement;
        LiveItems.LiveItem previous = previousAuthority.Copy();
        // One entry per id (OI-ID-06), asked before the ledger moves (LF9): a refusal here leaves
        // both items' homes as they were. Only an id the ledger's check missed gets this far.
        if (registry.TryGetByPersistentId(previousAuthority.PersistentItemId, out LostItemRecord holding) &&
            holding.NetId != item.NetId)
        {
            Multiplayer.LogError($"Lost and Found: collecting {item.name} (netId {item.NetId}, " +
                                 $"{previousAuthority.PersistentItemId:D}) was refused: " +
                                 $"{LostItemRegistryResult.DuplicatePersistentItemId} (netId {holding.NetId} has it)");
            Publish("item.lost-and-found-collection-rejected", item,
                new() { ["rejectionReason"] = "duplicate-persistent-item-id" }, DebugSeverity.Warning);
            return false;
        }
        // Distance collection takes only loose world items, except for an owner who isn't here, whose
        // items in trains and on shelves go too (lost-and-found.md, "The collection").
        bool ownerAway = !PlayerNumbers.IsConnected(keeper);
        if (!LiveItems.TryMoveToLostAndFound(item,
                out LiveItems.LiveItem authority, out string rejectionReason,
                allowRecoveryPlacement: reason != LostItemReason.OwnerDistance || ownerAway))
        {
            Publish("item.lost-and-found-collection-rejected", item,
                new() { ["rejectionReason"] = rejectionReason }, DebugSeverity.Warning);
            return false;
        }
        LostItemRecord record = new()
        {
            // The item's own id, not one of the record's: one id for life (ledger.md, "The entry").
            PersistentItemId = authority.PersistentItemId != Guid.Empty ? authority.PersistentItemId : Guid.NewGuid(),
            NetId = item.NetId,
            Revision = authority.Revision,
            OwnerIdentity = keeper.ToString("D"),
            PrefabName = authority.PrefabName,
            DisplayName = item.Item?.name ?? authority.PrefabName,
            AuthoredItemKey = item.AuthoredItemKey ?? string.Empty,
            InventoryClaimSlot = authority.InventoryClaimSlot,
            InventoryClaimFlags = (byte)authority.InventoryClaimFlags,
            Reason = reason,
            LostUtcTicks = DateTime.UtcNow.Ticks
        };
        LostItemRegistryResult added = registry.Add(record);
        if (added != LostItemRegistryResult.Added)
        {
            // A duplicate id here is one the ledger's check missed (OI-ID-06): logged like a refused restore.
            Multiplayer.LogError($"Lost and Found: collecting {item.name} (netId {item.NetId}, " +
                                 $"{authority.PersistentItemId:D}) was refused: {added}");
            RollBack(item, previous);
            return false;
        }
        ChangeCause<ItemRemovalReason> cause = CollectionCause(reason, keeper,
            ItemChangeCauses.Detail("was " + previousPlacement, detail));
        ItemUpdate destroy = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Destroy);
        if (destroy != null)
        {
            LiveItems.WriteToSnapshot(authority, destroy,
                ItemTransitionReason.LostAndFoundCollection);
            destroy.RemovalCause = cause;
        }
        try
        {
            ProjectToInertStorage(item);
            if (destroy != null)
                item.ApplyAuthorityMetadata(destroy);
        }
        catch (Exception exception)
        {
            registry.RemoveForRetrieval(item.NetId, keeper.ToString("D"),
                authority.Revision, out _);
            RollBack(item, previous);
            Multiplayer.LogException("Lost and Found collection failed", exception);
            return false;
        }
        eligibleSince.Remove(item.NetId);
        generation++;
        ItemChangeLog.RecordRemoval(item, cause);
        foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
        {
            player.NearbyItems.Remove(item);
            player.KnownItems.Remove(item);
            player.AcknowledgedWorldItems.Remove(item.NetId);
        }
        if (destroy != null)
            NetworkServer.Instance.SendItemUpdate(destroy);
        if (PlayerNumbers.TryGetSession(keeper,
                out ServerPlayer owner))
            NetworkServer.Instance.SendLostAndFoundList(owner);
        Publish("item.lost-and-found-collected", item, new()
        {
            ["ownerPlayerId"] = PlayerNumbers.Of(keeper),
            ["reason"] = reason.ToString(),
            ["generation"] = generation
        });
        return true;
    }

    // "CollectedToLostAndFound (OwnerDistance, owner Bentgear29; was World)".
    // A refused collection rolls back (J4). Off a coupler, a snap point or a tool's magazine, which the
    // collection took it off, it goes into the world where it is, and everyone is told.
    private static void RollBack(NetworkedItem item, LiveItems.LiveItem previous)
    {
        LiveItems.RollbackLostAndFoundCollection(item, previous);
        if (LostItemCollectionPolicy.RollsBackIntoWorld((LivePlacement)(byte)previous.Placement) &&
            LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record))
            DeletedCarItems.IntoWorld(item, record, "Lost and Found refused it, off where it was");
    }

    private static ChangeCause<ItemRemovalReason> CollectionCause(LostItemReason reason, Guid owner,
        string detail) =>
        ItemChangeLog.Removal(ItemRemovalReason.CollectedToLostAndFound, detail: ItemChangeCauses.Detail(
            reason + ", owner " + ItemChangeLog.PlayerName(owner), detail));


    public static void Clear()
    {
        registry.Clear();
        rows.Clear();
        dataSentTo.Clear();
        eligibleSince.Clear();
        lastEvaluations.Clear();
        generation = 0;
    }

    /// <summary>Detached main-thread diagnostics used by the in-game observability overlay.</summary>
    public static Dictionary<string, object>[] DebugSnapshot()
    {
        List<Dictionary<string, object>> result = new();
        if (NetworkLifecycle.Instance?.IsHost() == true)
        {
            foreach (LostItemRecord record in registry.GetAll())
                result.Add(DebugRegistryEntry(record));
            foreach (var pair in lastEvaluations.OrderBy(pair => pair.Key))
            {
                if (registry.TryGet(pair.Key, out _))
                    continue;
                Dictionary<string, object> entry = new(pair.Value, StringComparer.Ordinal)
                {
                    ["debugKey"] = $"candidate:{pair.Key}",
                    ["source"] = "host-policy"
                };
                if (NetworkedItem.TryGet(pair.Key, out NetworkedItem candidateItem) && candidateItem != null)
                {
                    foreach (var liveState in EntityDebugRegistry.ItemState(candidateItem))
                        if (!entry.ContainsKey(liveState.Key))
                            entry[liveState.Key] = liveState.Value;
                }
                entry["status"] = entry.TryGetValue("policyDecision", out object policyDecision)
                    ? policyDecision : "Candidate";
                result.Add(entry);
            }
        }
        // The host player's own list, as its session has it.
        if (ItemClient.Current?.LostAndFoundDebugSnapshot() is { } localList)
            result.AddRange(localList);
        return result.ToArray();
    }

    /// <summary>
    /// The load (docs/design/items/save-load.md): an entry for an item the load made, in its owner's
    /// Lost and Found. Its row is a reason and a time; its owner and claim are the ledger's.
    /// </summary>
    internal static bool RestoreEntry(NetworkedItem item, Guid owner, LostItemReason reason, long lostUtcTicks,
        string displayName, int claimSlot, ItemInventoryClaimFlags claimFlags)
    {
        if (item == null || item.NetId == 0 || owner == Guid.Empty)
            return false;
        LiveItems.LiveItem record = LiveItems.RestoreLostRecord(item, owner, 0, null,
            claimSlot, claimFlags, ItemMoveReason.RestoredFromSave);
        if (record == null)
            return false;
        LostItemRecord entry = new()
        {
            PersistentItemId = record.PersistentItemId,
            NetId = item.NetId,
            Revision = record.Revision,
            OwnerIdentity = owner.ToString("D"),
            PrefabName = record.PrefabName,
            DisplayName = string.IsNullOrEmpty(displayName) ? item.Item?.name ?? record.PrefabName : displayName,
            AuthoredItemKey = item.AuthoredItemKey ?? string.Empty,
            InventoryClaimSlot = claimSlot,
            InventoryClaimFlags = (byte)claimFlags,
            Reason = reason,
            LostUtcTicks = lostUtcTicks > 0 ? lostUtcTicks : DateTime.UtcNow.Ticks
        };
        LostItemRegistryResult result = registry.Restore(entry);
        LogRestore(result, entry.PersistentItemId, "load");
        if (result is not (LostItemRegistryResult.Added or LostItemRegistryResult.Updated))
            return false;
        ProjectToInertStorage(item);
        generation++;
        return true;
    }

    /// <summary>The save: every entry whose item is still there, for <c>Multiplayer.Items</c>' rows.</summary>
    internal static IReadOnlyList<LostItemRecord> Entries()
    {
        ReconcileCanonicalPlacements();
        return registry.GetAll();
    }

    internal static bool TryGetEntry(Guid itemId, out LostItemRecord entry) =>
        registry.TryGetByPersistentId(itemId, out entry);

    private static void LogRestore(LostItemRegistryResult result, Guid persistentItemId, string during)
    {
        if (result is not (LostItemRegistryResult.Added or LostItemRegistryResult.Updated))
            Multiplayer.LogError($"Lost and Found: restoring entry {persistentItemId:D} ({during}) was refused: {result}");
    }

    private static LostItemDecision Evaluate(LostItemCandidate candidate) =>
        LostItemCollectionPolicy.Evaluate(candidate, Multiplayer.Settings.LostItemOwnerDistance,
            Multiplayer.Settings.LostItemNearbyPlayerProtectionDistance,
            Multiplayer.Settings.LostItemCollectionGraceSeconds);

    private static bool HasPosition(ServerPlayer player) =>
        player.TrackingData.Position.HasValue || NetworkLifecycle.Instance.IsHost(player);

    // A grace period from their first position after their load, not from the load itself: after a
    // restart a client's game can take longer than the grace to send where it put them (OI-JOIN-10).
    // One standing still sends none: then from a minute after the load (K5).
    private static bool HasSettledPosition(ServerPlayer player, float now) =>
        NetworkLifecycle.Instance.IsHost(player) || player.TrackingData.Position.HasValue &&
        player.LoadingState == PlayerLoadingState.Complete && player.PositionSettlesFrom >= 0f &&
        now - player.PositionSettlesFrom >= Multiplayer.Settings.LostItemCollectionGraceSeconds;

    private static LostItemCandidate BuildCandidate(NetworkedItem item,
        LiveItems.LiveItem authority, float now)
    {
        Guid keeper = LiveItems.KeeperOf(authority);
        // A just-joined owner's items were collected seconds after they loaded (ledger-save on a
        // dedicated host, 2026-10-21): before their game sends a position it reads as the origin, and
        // its first ones can be from before it put the player at their saved spot. An owner's
        // position counts a grace period after the first one their loaded game sent.
        bool ownerKnown = PlayerNumbers.TryGetSession(
            keeper, out ServerPlayer owner) && HasSettledPosition(owner, now);
        List<ServerPlayer> players = NetworkServer.Instance.ServerPlayers
            .Where(player => player != null && player.LoadingState >= PlayerLoadingState.ReadyForItems && HasPosition(player))
            .ToList();
        float nearest = players.Count == 0 ? float.MaxValue :
            players.Min(player => (player.WorldPosition - item.transform.position).sqrMagnitude);
        bool contained = false;
        try { contained = StorageController.Instance?.StorageItemContainers?.ContainsItem(item.Item) == true; }
        catch { }
        return new LostItemCandidate
        {
            NetId = item.NetId,
            Revision = authority.Revision,
            Owner = keeper,
            OwnerConnected = PlayerNumbers.IsConnected(keeper),
            Placement = (LivePlacement)(byte)authority.Placement,
            IsGrabbed = item.Item?.IsGrabbed() == true,
            IsSnapped = item.Item?.SnappableItem?.SnappedTo != null,
            IsInMachine = contained,
            IsLicensePaper = IsLicensePaper(item),
            OwnerPositionKnown = ownerKnown,
            OwnerDistanceSquared = ownerKnown
                ? (owner.WorldPosition - item.transform.position).sqrMagnitude : float.MaxValue,
            NearestPlayerPositionKnown = players.Count > 0,
            NearestPlayerDistanceSquared = nearest,
            EligibleDurationSeconds = eligibleSince.TryGetValue(item.NetId, out float since)
                ? now - since : 0
        };
    }

    private static void ProjectToInertStorage(NetworkedItem item)
    {
        StorageIntegration.ProjectLostAndFound(item?.Item);
    }

    private static void RestoreFromProjection(NetworkedItem item)
    {
        StorageIntegration.RestoreLostAndFound(item?.Item);
    }

    private static bool IsLicensePaper(NetworkedItem item)
    {
        string prefab = item?.Item?.InventorySpecs?.ItemPrefabName ?? string.Empty;
        string type = item?.TrackedItemType?.Name ?? string.Empty;
        return prefab.IndexOf("License", StringComparison.OrdinalIgnoreCase) >= 0 ||
            type.IndexOf("License", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsLicensePaper(ItemBase item)
    {
        string prefab = item?.InventorySpecs?.ItemPrefabName ?? string.Empty;
        return prefab.IndexOf("License", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsInLocalInventory(NetworkedItem item)
    {
        if (item?.Item == null)
            return false;
        // includeDropped=false is deliberate: an essential world item keeps an immutable
        // dropped silhouette in inventory, but that silhouette must not make the physical
        // world object look inventoried to the collection policy.
        if (InventoryIntegration.ContainsActive(item.gameObject))
            return true;
        return (StorageIntegration.GetMembership(item.Item) & StorageMembership.Inventory) != 0;
    }

    private static void ReconcileCanonicalPlacements()
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        foreach (LostItemRecord lost in registry.GetAll())
        {
            bool itemPresent = NetworkedItem.TryGet(lost.NetId, out NetworkedItem item) && item != null;
            SavedItemRow row = null;
            bool data = !itemPresent && rows.TryGetValue(lost.PersistentItemId, out row);
            // A booklet kept as data goes when its job ends, as its object would (OI-COLD-08).
            if (data && JobPapers.IsPaper(lost.PrefabName) && !JobPaperRows.Wanted(row, out _))
            {
                RemoveStaleEntry(lost.NetId, "canonical-item-destroyed");
                continue;
            }
            LivePlacement? placement = LiveItems.TryGet(lost.NetId,
                out LiveItems.LiveItem authority)
                ? (LivePlacement)(byte)authority.Placement : null;
            switch (LostItemEntryReconciler.Classify(itemPresent || data, placement))
            {
                case LostItemEntryStatus.Stored:
                    if (data)
                        break;
                    // Entries restored from a save bound to items the load left active where they
                    // were dropped: in Lost and Found and lying in the world at once. Keep them inert.
                    if (item.gameObject.activeSelf)
                        ProjectToInertStorage(item);
                    ToData(item, lost);
                    break;
                case LostItemEntryStatus.PlacementChanged:
                    RemoveStaleEntry(lost.NetId, "canonical-placement-changed");
                    break;
                case LostItemEntryStatus.ItemDestroyed:
                    // The job system destroys every booklet copy when its job completes, expires
                    // or is abandoned, including one stored here.
                    RemoveStaleEntry(lost.NetId, "canonical-item-destroyed");
                    break;
            }
        }
    }

    /// <summary>Drops an entry whose item the server is about to replace (an item remade fresh).</summary>
    internal static void Forget(ushort itemNetId, string reason) => RemoveStaleEntry(itemNetId, reason);

    private static void RemoveStaleEntry(ushort itemNetId, string reason)
    {
        if (registry.RemoveStale(itemNetId, out LostItemRecord removed) !=
            LostItemRegistryResult.Removed)
            return;
        dataSentTo.Remove(itemNetId);
        if (rows.Remove(removed.PersistentItemId) && LiveItems.TryGet(itemNetId, out LiveItems.LiveItem gone))
            PlayerItems.LostDataGone(gone);
        generation++;
        eligibleSince.Remove(itemNetId);
        Multiplayer.Log($"[LostAndFound] Removed entry handle={removed.Handle}, netId={itemNetId}, " +
            $"prefab={removed.PrefabName}, owner={removed.OwnerIdentity}: {reason}");
        if (PlayerNumbers.TryGetSession(OwnerOf(removed),
                out ServerPlayer owner))
            NetworkServer.Instance.SendLostAndFoundList(owner);
        Publish("item.lost-and-found-entry-invalidated", itemNetId, new()
        {
            ["ownerPlayerId"] = PlayerNumbers.Of(OwnerOf(removed)),
            ["prefabName"] = removed.PrefabName,
            ["reason"] = reason,
            ["generation"] = generation
        }, DebugSeverity.Warning);
    }

    private static void TraceRecallRejection(ushort itemNetId, string reason, uint lostHandle = 0)
    {
        global::Multiplayer.Multiplayer.LogWarning($"[LostAndFound Recall] Manager retrieval rejected: " +
            $"handle={lostHandle}, netId={itemNetId}, reason={reason ?? string.Empty}");
    }

    public static void SetInventoryClaimSlot(ushort itemNetId, int slot) =>
        registry.SetInventoryClaimSlot(itemNetId, slot);

    /// <summary>Whether a Lost and Found entry belongs to this player ID.</summary>
    internal static bool HasOwner(Guid owner) => registry.HasOwner(owner.ToString("D"));

    /// <summary>A player's ID changed (InstallGuidRekey): their entries follow. Returns how many changed.</summary>
    internal static int RekeyOwner(Guid from, Guid to)
    {
        int changed = registry.RekeyOwner(from.ToString("D"), to.ToString("D"));
        foreach (SavedItemRow row in rows.Values.Where(row => row.OwnerIdentity == from))
            row.OwnerIdentity = to;
        if (changed > 0)
            generation++;
        return changed;
    }

    /// <summary>
    /// An entry's item took a new id (a copy re-minted by the one-home repair, D7): its entry and its
    /// data row follow, so it is saved and found under the new id. False when it has no entry.
    /// </summary>
    internal static bool RekeyItem(ushort itemNetId, Guid fresh)
    {
        if (!registry.TryGet(itemNetId, out LostItemRecord lost))
            return false;
        Guid from = lost.PersistentItemId;
        bool heldFrom = registry.TryGetByPersistentId(from, out LostItemRecord holder) && holder.NetId == itemNetId;
        if (!registry.RekeyItem(itemNetId, fresh))
            return false;
        if (heldFrom && rows.TryGetValue(from, out SavedItemRow row))
        {
            rows.Remove(from);
            row.PersistentItemId = fresh;
            rows[fresh] = row;
        }
        generation++;
        // Its owner's open list names it as it was: sent again, so their first click isn't refused.
        if (PlayerNumbers.TryGetSession(OwnerOf(lost), out ServerPlayer owner))
            NetworkServer.Instance.SendLostAndFoundList(owner);
        return true;
    }

    private static Guid OwnerOf(LostItemRecord record) =>
        Guid.TryParse(record.OwnerIdentity, out Guid owner) ? owner : Guid.Empty;

    private static ItemRow ToRow(LostItemRecord record) => new()
    {
        Handle = ItemHandles.Of(record.PersistentItemId),
        PrefabName = record.PrefabName ?? string.Empty,
        DisplayName = record.DisplayName ?? string.Empty,
        Where = record.Reason.ToString(),
        Slot = record.InventoryClaimSlot,
        Revision = record.Revision
    };

    private static Dictionary<string, object> DebugRegistryEntry(LostItemRecord record)
    {
        NetworkedItem.TryGet(record.NetId, out NetworkedItem item);
        Dictionary<string, object> entry = item == null
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            : new Dictionary<string, object>(EntityDebugRegistry.ItemState(item), StringComparer.Ordinal);
        entry["debugKey"] = $"registry-handle:{record.Handle}";
        entry["source"] = "host-registry";
        entry["status"] = "Stored";
        entry["persistentItemId"] = record.PersistentItemId.ToString("D");
        entry["lostHandle"] = record.Handle;
        entry["netId"] = record.NetId;
        entry["revision"] = record.Revision;
        entry["ownerPlayerId"] = PlayerNumbers.Of(OwnerOf(record));
        entry["ownerIdentity"] = record.OwnerIdentity;
        entry["prefabName"] = record.PrefabName;
        entry["displayName"] = record.DisplayName;
        entry["authoredItemKey"] = record.AuthoredItemKey ?? string.Empty;
        entry["reason"] = record.Reason.ToString();
        entry["lostUtc"] = FormatUtc(record.LostUtcTicks);
        entry["inventoryClaimSlot"] = record.InventoryClaimSlot;
        entry["inventoryClaimFlagsRaw"] = record.InventoryClaimFlags;
        entry["canonicalUnityObjectPresent"] = item != null;
        if (LiveItems.TryGet(record.NetId, out LiveItems.LiveItem authority))
        {
            entry["authorityPlacement"] = authority.Placement.ToString();
            entry["authorityRevision"] = authority.Revision;
            entry["authorityPlacementPlayerId"] = PlayerNumbers.Of(authority.Holder);
            entry["authorityTransitionReason"] = authority.LastReason.ToString();
        }
        return entry;
    }

    private static void RecordEvaluation(NetworkedItem item,
        LiveItems.LiveItem authority, LostItemDecision decision,
        LostItemCandidate candidate)
    {
        Dictionary<string, object> entry = new(StringComparer.Ordinal)
        {
            ["netId"] = item.NetId,
            ["displayName"] = item.Item?.name ?? authority.PrefabName,
            ["prefabName"] = authority.PrefabName,
            ["revision"] = authority.Revision,
            ["ownerPlayerId"] = PlayerNumbers.Of(authority.Owner),
            ["authorityPlacement"] = authority.Placement.ToString(),
            ["policyDecision"] = decision.Kind.ToString(),
            ["policyReason"] = decision.Reason,
            ["ownerPositionKnown"] = candidate.OwnerPositionKnown,
            ["ownerDistance"] = candidate.OwnerPositionKnown
                ? Mathf.Sqrt(candidate.OwnerDistanceSquared) : -1f,
            ["nearestPlayerPositionKnown"] = candidate.NearestPlayerPositionKnown,
            ["nearestPlayerDistance"] = candidate.NearestPlayerPositionKnown
                ? Mathf.Sqrt(candidate.NearestPlayerDistanceSquared) : -1f,
            ["eligibleSeconds"] = candidate.EligibleDurationSeconds,
            ["requiredGraceSeconds"] = Multiplayer.Settings.LostItemCollectionGraceSeconds,
            ["isGrabbed"] = candidate.IsGrabbed,
            ["isSnapped"] = candidate.IsSnapped,
            ["isInMachine"] = candidate.IsInMachine,
            ["isLicensePaper"] = candidate.IsLicensePaper
        };
        lastEvaluations[item.NetId] = entry;
    }

    private static string FormatUtc(long ticks)
    {
        try { return new DateTime(ticks, DateTimeKind.Utc).ToString("O"); }
        catch { return $"invalid-ticks:{ticks}"; }
    }

    private static void TraceDecision(NetworkedItem item, LiveItems.LiveItem authority,
        LostItemDecision decision, LostItemCandidate candidate)
    {
        if (!DebugRuntime.EnabledFor("inventory")) return;
        Publish("item.lost-and-found-evaluated", item, new()
        {
            ["decision"] = decision.Kind.ToString(),
            ["reason"] = decision.Reason,
            ["ownerPlayerId"] = PlayerNumbers.Of(authority.Owner),
            ["ownerDistance"] = Mathf.Sqrt(candidate.OwnerDistanceSquared),
            ["nearestPlayerDistance"] = Mathf.Sqrt(candidate.NearestPlayerDistanceSquared),
            ["eligibleSeconds"] = candidate.EligibleDurationSeconds
        });
    }

    private static void Publish(string eventName, NetworkedItem item,
        Dictionary<string, object> data, DebugSeverity severity = DebugSeverity.Info) =>
        Publish(eventName, item?.NetId ?? 0, data, severity);

    private static void Publish(string eventName, ushort itemNetId,
        Dictionary<string, object> data, DebugSeverity severity = DebugSeverity.Info)
    {
        if (!DebugRuntime.EnabledFor("inventory")) return;
        DebugRuntime.Publish("inventory", eventName, DebugRuntimeSide.Server, severity,
            "Item", itemNetId.ToString(), data);
    }
}
