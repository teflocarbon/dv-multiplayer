using DV.CabControls;
using DV.InventorySystem;
using DV.JObjectExtstensions;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.ChangeCauses;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Items.WorldItems;
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

/// <summary>Host-authoritative virtual Lost and Found. Vanilla storage is an inert projection.</summary>
public static class NetworkedLostAndFoundManager
{
    private const uint ScanIntervalTicks = 24;
    private static uint lastScanTick; // the tick can advance by more than one a frame
    private static readonly LostItemRegistry registry = new();
    private static readonly Dictionary<ushort, float> eligibleSince = new();
    private static readonly List<JObject> pendingSaveImports = new();

    /// <summary>Prefab names of saved Lost and Found items, not yet restored, that belong to this owner.</summary>
    internal static IEnumerable<string> PendingPrefabsOwnedBy(Guid owner) =>
        pendingSaveImports
            .Where(value => Guid.TryParse((string)value["ownerIdentity"], out Guid identity) && identity == owner)
            .Select(value => (string)value["prefabName"] ?? string.Empty)
            .Where(prefab => prefab.Length > 0);
    private static readonly Dictionary<ushort, Dictionary<string, object>> lastEvaluations = new();
    private static uint generation;
    private static bool saveImportAttempted;

    /// <summary>The save's records have been read (the owned-items ledger is checked against them after).</summary>
    internal static bool ImportAttempted => saveImportAttempted;

    public static uint Generation => generation;
    public static bool Contains(ushort itemNetId) => registry.TryGet(itemNetId, out _);
    /// <summary>The id of an item's entry: its own persistent id (owned-items.md), or empty for none.</summary>
    public static Guid EntryId(ushort itemNetId) =>
        registry.TryGet(itemNetId, out LostItemRecord record) ? record.PersistentItemId : Guid.Empty;
    public static int Count => registry.Count;

    public static bool TryCreateCollectionProjection(NetworkedItem item,
        out ItemUpdateData snapshot)
    {
        snapshot = null;
        if (item == null || !registry.TryGet(item.NetId, out LostItemRecord lost) ||
            !AuthoritativeItemRegistry.TryGet(item.NetId,
                out AuthoritativeItemRegistry.Record authority) ||
            authority.Placement != ItemPlacementKind.LostAndFound)
            return false;

        snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Destroy);
        if (snapshot == null)
            return false;
        AuthoritativeItemRegistry.WriteToSnapshot(authority, snapshot,
            ItemTransitionReason.LostAndFoundCollection);
        snapshot.AuthorityRevision = lost.Revision;
        snapshot.RemovalCause = CollectionCause(lost.Reason, lost.OwnerPlayerId, null);
        return true;
    }

    public static void HostTick(uint tick)
    {
        if (!NetworkLifecycle.Instance.IsHost() || unchecked(tick - lastScanTick) < ScanIntervalTicks)
            return;
        lastScanTick = tick;
        TryImportSave();
        TryBindPendingSaveEntries();
        ReconcileCanonicalPlacements();
        float now = Time.realtimeSinceStartup;
        bool capturePolicyDiagnostics = DebugOverlayController.LostAndFoundDiagnosticsVisible;
        if (!capturePolicyDiagnostics && lastEvaluations.Count > 0)
            lastEvaluations.Clear();
        HashSet<ushort> seen = new();
        foreach (NetworkedItem item in NetworkedItem.GetAll().Where(value => value != null).Distinct().ToArray())
        {
            if (item.NetId == 0 || !AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record authority))
                continue;
            seen.Add(item.NetId);
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

    public static LostItemData[] SnapshotFor(ServerPlayer player)
    {
        TryBindPendingSaveEntries();
        ReconcileCanonicalPlacements();
        return player == null ? Array.Empty<LostItemData>() :
            registry.GetForOwner(player.PlayerId).Select(ToData).ToArray();
    }

    public static bool TryRetrieve(ServerPlayer player, uint lostHandle, uint expectedRevision,
        int requestedSlot, int existingItemSlot, int inventoryCapacity, int[] occupiedSlots,
        out ItemUpdateData snapshot, out string rejectionReason)
    {
        snapshot = null;
        rejectionReason = string.Empty;
        global::Multiplayer.Multiplayer.Log($"[LostAndFound Recall] Manager retrieval started: " +
            $"player=P{player?.PlayerId ?? 0}, handle={lostHandle}, expectedRevision={expectedRevision}, " +
            $"requestedSlot={requestedSlot}, existingSlot={existingItemSlot}, capacity={inventoryCapacity}, " +
            $"registryContains={registry.TryGetByHandle(lostHandle, out _)}");
        if (player == null || !registry.TryGetByHandle(lostHandle, out LostItemRecord lost))
        {
            rejectionReason = "unknown-lost-item";
            TraceRecallRejection(0, rejectionReason, lostHandle);
            return false;
        }
        ushort itemNetId = lost.NetId;
        if (inventoryCapacity < 1 || inventoryCapacity > 256)
        {
            rejectionReason = "inventory-unavailable";
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        LostItemRetrievalPlan plan = LostItemRetrievalPlanner.Plan(lost, player.PlayerId,
            expectedRevision, requestedSlot, existingItemSlot, inventoryCapacity,
            occupiedSlots ?? Array.Empty<int>());
        if (!plan.Accepted)
        {
            rejectionReason = plan.Reason;
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        global::Multiplayer.Multiplayer.Log($"[LostAndFound Recall] Retrieval plan accepted: " +
            $"netId={itemNetId}, targetSlot={plan.TargetSlot}, storedRevision={lost.Revision}, " +
            $"owner=P{lost.OwnerPlayerId}");
        if (!NetworkedItem.TryGet(itemNetId, out NetworkedItem item) || item == null)
        {
            // Nothing is left to retrieve: drop the entry so the owner's list stops offering it.
            RemoveStaleEntry(itemNetId, "canonical-item-destroyed");
            rejectionReason = "missing-canonical-unity-item";
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        if (!AuthoritativeItemRegistry.TryGet(itemNetId, out AuthoritativeItemRegistry.Record authority) ||
            authority.Placement != ItemPlacementKind.LostAndFound)
        {
            RemoveStaleEntry(itemNetId, "canonical-item-no-longer-in-lost-and-found");
            rejectionReason = "item-not-in-lost-and-found";
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        if (!AuthoritativeItemRegistry.TryRetrieveFromLostAndFound(item, player,
                expectedRevision, plan.TargetSlot, out snapshot, out rejectionReason))
        {
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        global::Multiplayer.Multiplayer.Log($"[LostAndFound Recall] Authority transition accepted: " +
            $"netId={itemNetId}, newRevision={snapshot?.AuthorityRevision ?? 0}, " +
            $"state={snapshot?.ItemState.ToString() ?? "null"}, targetSlot={plan.TargetSlot}");
        LostItemRegistryResult removed = registry.RemoveForRetrieval(itemNetId, player.PlayerId,
            expectedRevision, out LostItemRecord removedRecord);
        if (removed != LostItemRegistryResult.Removed)
        {
            AuthoritativeItemRegistry.RestoreLostRecord(item, player.PlayerId,
                expectedRevision, lost.PrefabName, lost.InventoryClaimPlayerId,
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
            AuthoritativeItemRegistry.RestoreLostRecord(item, player.PlayerId,
                expectedRevision, lost.PrefabName, lost.InventoryClaimPlayerId,
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
            NetworkServer.Instance.SendLostItemsSnapshot(owner);
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
        uint expectedRevision, int existingItemSlot, out ItemUpdateData snapshot,
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
            existingItemSlot, existingItemSlot + 1, Array.Empty<int>(), out snapshot,
            out rejectionReason);
    }

    /// <summary>
    /// A car is about to be deleted (CarDeletePlayerSafetyPatch): the items lying in it would go to
    /// the car pool with it, leaving records that point at a car that no longer exists, in nobody's
    /// reach and out of recall (a player's radio, 2026-10-17). A player's own item goes to their
    /// Lost and Found; anything else goes with the car, as in single player, but cleanly.
    /// </summary>
    public static void CollectFromDeletedCar(TrainCar car)
    {
        if (car == null || NetworkLifecycle.Instance?.IsHost() != true)
            return;
        NetworkedTrainCar networkedCar = car.GetComponent<NetworkedTrainCar>();
        if (networkedCar == null || networkedCar.NetId == 0)
            return;
        var inCar = AuthoritativeItemRegistry.GetAll()
            .Where(record => record.Placement == ItemPlacementKind.TrainInterior && record.WorldParentNetId == networkedCar.NetId)
            .Select(record => record.NetId).ToList();
        foreach (ushort netId in inCar)
        {
            if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null)
                continue;
            // Off the car first, so the pool doesn't take it.
            item.transform.SetParent(WorldMover.OriginShiftParent, true);
            if (Collect(item, LostItemReason.ContainerCleared, $"car {car.ID} deleted"))
                continue;
            item.NoteRemovalCause(ItemChangeLog.Removal(ItemRemovalReason.DestroyedWithCar,
                detail: $"car {car.ID} deleted, not a player's item"));
            UnityEngine.Object.Destroy(item.gameObject); // NetworkedItem.OnDestroy replicates the destroy
        }
    }

    /// <param name="detail">What the reason leaves out, for the logged cause (the deleted car).</param>
    public static bool Collect(NetworkedItem item, LostItemReason reason, string detail = null)
    {
        if (!NetworkLifecycle.Instance.IsHost() || item == null || item.NetId == 0)
            return false;
        if (registry.TryGet(item.NetId, out _))
            return false;
        if (item.Item?.InventorySpecs?.BelongsToPlayer != true)
        {
            Publish("item.lost-and-found-collection-rejected", item,
                new() { ["rejectionReason"] = "not-a-personal-item" }, DebugSeverity.Warning);
            return false;
        }
        if (IsInLocalInventory(item))
        {
            Publish("item.lost-and-found-collection-rejected", item,
                new() { ["rejectionReason"] = "item-still-in-local-inventory" }, DebugSeverity.Warning);
            return false;
        }
        uint previousRevision = item.AuthorityRevision;
        if (!AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record previousAuthority))
            return false;
        ItemPlacementKind previousPlacement = previousAuthority.Placement;
        byte previousPlacementPlayerId = previousAuthority.PlacementPlayerId;
        ItemTransitionReason previousReason = previousAuthority.LastReason;
        // Distance collection takes only loose world items, except for an owner who isn't here, whose
        // items in trains and on shelves go too (owned-items.md, PR 3).
        bool ownerAway = previousAuthority.PersistentOwnerPlayerId == ItemTransitionCommand.AbsentOwnerId;
        if (!AuthoritativeItemRegistry.TryMoveToLostAndFound(item,
                out AuthoritativeItemRegistry.Record authority, out string rejectionReason,
                allowRecoveryPlacement: reason != LostItemReason.OwnerDistance || ownerAway))
        {
            Publish("item.lost-and-found-collection-rejected", item,
                new() { ["rejectionReason"] = rejectionReason }, DebugSeverity.Warning);
            return false;
        }
        LostItemRecord record = new()
        {
            // The item's own id, not one of the record's: one id for life (owned-items.md).
            PersistentItemId = authority.PersistentItemId != Guid.Empty ? authority.PersistentItemId : Guid.NewGuid(),
            NetId = item.NetId,
            Revision = authority.Revision,
            OwnerPlayerId = authority.PersistentOwnerPlayerId,
            OwnerIdentity = authority.PersistentOwnerIdentity != Guid.Empty
                ? authority.PersistentOwnerIdentity.ToString("D")
                : OwnerIdentity(authority.PersistentOwnerPlayerId),
            PrefabName = authority.PrefabName,
            DisplayName = item.Item?.name ?? authority.PrefabName,
            AuthoredItemKey = item.AuthoredItemKey ?? string.Empty,
            InventoryClaimPlayerId = authority.InventoryClaimPlayerId,
            InventoryClaimSlot = authority.InventoryClaimSlot,
            InventoryClaimFlags = (byte)authority.InventoryClaimFlags,
            Reason = reason,
            LostUtcTicks = DateTime.UtcNow.Ticks
        };
        if (registry.Add(record) != LostItemRegistryResult.Added)
        {
            AuthoritativeItemRegistry.RollbackLostAndFoundCollection(item, previousRevision,
                previousPlacement, previousPlacementPlayerId, previousReason);
            return false;
        }
        ChangeCause<ItemRemovalReason> cause = CollectionCause(reason, authority.PersistentOwnerPlayerId,
            ItemChangeCauses.Detail("was " + previousPlacement, detail));
        ItemUpdateData destroy = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Destroy);
        if (destroy != null)
        {
            AuthoritativeItemRegistry.WriteToSnapshot(authority, destroy,
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
            registry.RemoveForRetrieval(item.NetId, authority.PersistentOwnerPlayerId,
                authority.Revision, out _);
            AuthoritativeItemRegistry.RollbackLostAndFoundCollection(item, previousRevision,
                previousPlacement, previousPlacementPlayerId, previousReason);
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
            NetworkServer.Instance.SendItemUpdatePacket(destroy);
        if (NetworkServer.Instance.TryGetServerPlayer(authority.PersistentOwnerPlayerId,
                out ServerPlayer owner))
            NetworkServer.Instance.SendLostItemsSnapshot(owner);
        Publish("item.lost-and-found-collected", item, new()
        {
            ["ownerPlayerId"] = authority.PersistentOwnerPlayerId,
            ["reason"] = reason.ToString(),
            ["generation"] = generation
        });
        return true;
    }

    // "CollectedToLostAndFound (OwnerDistance, owner Bentgear29; was World)".
    private static ChangeCause<ItemRemovalReason> CollectionCause(LostItemReason reason, byte ownerPlayerId,
        string detail) =>
        ItemChangeLog.Removal(ItemRemovalReason.CollectedToLostAndFound, detail: ItemChangeCauses.Detail(
            reason + ", owner " + (ItemChangeLog.PlayerName(ownerPlayerId) ?? "P" + ownerPlayerId), detail));


    public static void Clear()
    {
        registry.Clear();
        eligibleSince.Clear();
        pendingSaveImports.Clear();
        lastEvaluations.Clear();
        generation = 0;
        saveImportAttempted = false;
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
            int pendingIndex = 0;
            foreach (JObject pending in pendingSaveImports)
            {
                result.Add(new Dictionary<string, object>
                {
                    ["debugKey"] = $"pending-save:{pendingIndex++}",
                    ["source"] = "save-import",
                    ["status"] = "AwaitingRebind",
                    ["netId"] = 0,
                    ["revision"] = (uint?)pending["revision"] ?? 0,
                    ["ownerIdentity"] = (string)pending["ownerIdentity"] ?? string.Empty,
                    ["prefabName"] = (string)pending["prefabName"] ?? string.Empty,
                    ["displayName"] = (string)pending["displayName"] ?? string.Empty,
                    ["reason"] = ((LostItemReason)((byte?)pending["reason"] ?? 0)).ToString(),
                    ["persistentItemId"] = (string)pending["persistentItemId"] ?? string.Empty,
                    ["storageIndex"] = (int?)pending["storageIndex"] ?? -1
                });
            }
        }
        // The host player's own list, as its session has it.
        if (ItemClient.Current?.LostAndFoundDebugSnapshot() is { } localList)
            result.AddRange(localList);
        return result.ToArray();
    }

    public static void WriteSave(JObject multiplayerRoot)
    {
        if (multiplayerRoot == null) return;
        TryImportSave();
        TryBindPendingSaveEntries();
        // If DV asks for a save before starting items/storage are ready, leave the previously
        // loaded semantic array untouched. A later save will replace it from the live registry.
        if (!saveImportAttempted)
            return;
        // Don't save an entry whose item is gone: with no storage slot it could never bind again.
        ReconcileCanonicalPlacements();
        JArray values = new();
        foreach (LostItemRecord record in registry.GetAll())
            values.Add(Serialize(record));
        // An owner's runtime player ID is not available until they reconnect. Preserve their
        // unbound semantic records across intermediate saves rather than silently losing them.
        foreach (JObject pending in pendingSaveImports)
            values.Add(pending.DeepClone());
        multiplayerRoot["LostAndFound"] = values;
    }

    private static void TryImportSave()
    {
        if (saveImportAttempted || StartingItemsController.Instance == null ||
            !StartingItemsController.Instance.itemsLoaded || SaveGameManager.Instance?.data == null)
            return;
        StorageController storage = StorageController.Instance;
        if (storage?.StorageLostAndFound == null)
            return;
        saveImportAttempted = true;
        JObject root = SaveGameManager.Instance.data.GetJObject("Multiplayer");
        JArray saved = root?["LostAndFound"] as JArray;
        List<ItemBase> available = storage.StorageLostAndFound.GetStorageItemList()
            .Where(item => item != null).ToList();

        // Papers are presentation debris, including the vanilla zero-origin safeguard path.
        foreach (ItemBase license in available.Where(IsLicensePaper).ToArray())
        {
            storage.RemoveItemFromStorageItemList(storage.StorageLostAndFound, license);
            available.Remove(license);
            UnityEngine.Object.Destroy(license.gameObject);
        }
        if (saved != null)
            pendingSaveImports.AddRange(saved.OfType<JObject>().Select(value => (JObject)value.DeepClone()));
        UseItemIds(storage.StorageLostAndFound.GetStorageItemList());
        TryBindPendingSaveEntries();
    }

    /// <summary>A saved entry not yet bound (its owner hasn't joined), for the owned-items ledger.</summary>
    internal readonly struct PendingEntry
    {
        public PendingEntry(Guid itemId, Guid owner, string prefab, int claimSlot)
        {
            ItemId = itemId;
            Owner = owner;
            Prefab = prefab;
            ClaimSlot = claimSlot;
        }

        public Guid ItemId { get; }
        public Guid Owner { get; }
        public string Prefab { get; }
        public int ClaimSlot { get; }
    }

    internal static IEnumerable<PendingEntry> PendingEntries()
    {
        foreach (JObject value in pendingSaveImports)
        {
            if (!Guid.TryParse((string)value["persistentItemId"], out Guid id) || id == Guid.Empty ||
                !Guid.TryParse((string)value["ownerIdentity"], out Guid owner))
                continue;
            ItemInventoryClaimFlags flags = (ItemInventoryClaimFlags)((byte?)value["inventoryClaimFlags"] ?? 0);
            bool retrievalClaim = (flags & (ItemInventoryClaimFlags.Reserved | ItemInventoryClaimFlags.Locked)) != 0;
            yield return new PendingEntry(id, owner, (string)value["prefabName"] ?? string.Empty,
                retrievalClaim ? (int?)value["inventoryClaimSlot"] ?? -1 : -1);
        }
    }

    // An entry and its stored item have one id from the load on. Vanilla saves a stored item's
    // state, and with it the item's id, only when the item has ItemSaveData (StorageSerializer): a
    // mug has none, comes back with no id and is given a new one. Then the entry's id is the item's
    // and the item takes it back. An item with saved state keeps its own id, and an entry from a save
    // before the item's id was the entry's (a record id minted per collection) takes it.
    private static void UseItemIds(List<ItemBase> restoredItems)
    {
        HashSet<ItemBase> taken = new();
        foreach (JObject value in pendingSaveImports)
        {
            Guid.TryParse((string)value["persistentItemId"], out Guid saved);
            ItemBase stored = FindStoredItem(restoredItems, restoredItems.Where(item => item != null && !taken.Contains(item)).ToList(),
                saved, (int?)value["storageIndex"] ?? -1, (string)value["prefabName"] ?? string.Empty);
            if (stored == null)
                continue;
            taken.Add(stored);
            Guid itemId = ItemIdOf(stored);
            if (itemId == saved || saved == Guid.Empty && itemId == Guid.Empty)
                continue;
            if (itemId != Guid.Empty && stored.GetComponent<ItemSaveData>() != null)
            {
                Multiplayer.LogDebug(() => $"Lost and Found: saved entry {saved:D} takes its item's id {itemId:D}");
                value["persistentItemId"] = itemId.ToString("D");
            }
            else if (saved != Guid.Empty)
            {
                Multiplayer.LogDebug(() => $"Lost and Found: stored {stored.name} takes its entry's id {saved:D}");
                GiveId(stored, saved);
            }
        }
    }

    private static void GiveId(ItemBase stored, Guid id)
    {
        PersistentWorldItemIdentity identity = stored.GetComponent<PersistentWorldItemIdentity>() ??
                                               stored.gameObject.AddComponent<PersistentWorldItemIdentity>();
        identity.Value = id;
        if (NetworkedItem.TryGetNetworkedItem(stored, out NetworkedItem item) && item.NetId != 0 &&
            AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record))
            record.PersistentItemId = OwnedItemsServer.ClaimPersistentId(item, id);
    }

    private static Guid ItemIdOf(ItemBase item) =>
        item != null && item.TryGetComponent(out PersistentWorldItemIdentity identity) ? identity.Value : Guid.Empty;

    // The stored item an entry binds to: the one with the entry's id, else (an older save) the one
    // at its storage index if the prefab matches, else the first with its prefab.
    private static ItemBase FindStoredItem(List<ItemBase> restoredItems, List<ItemBase> available, Guid id,
        int storageIndex, string prefab)
    {
        ItemBase byId = id == Guid.Empty ? null : available.FirstOrDefault(candidate => ItemIdOf(candidate) == id);
        if (byId != null)
            return byId;
        ItemBase baseItem = storageIndex >= 0 && storageIndex < restoredItems.Count ? restoredItems[storageIndex] : null;
        if (baseItem == null || !available.Contains(baseItem) ||
            !string.Equals(baseItem.InventorySpecs?.ItemPrefabName, prefab, StringComparison.Ordinal))
            baseItem = available.FirstOrDefault(candidate =>
                string.Equals(candidate.InventorySpecs?.ItemPrefabName, prefab, StringComparison.Ordinal));
        return baseItem;
    }

    private static void TryBindPendingSaveEntries()
    {
        if (pendingSaveImports.Count == 0 || NetworkServer.Instance == null)
            return;
        StorageController storage = StorageController.Instance;
        if (storage?.StorageLostAndFound == null)
            return;
        int countBefore = registry.Count;
        List<ItemBase> restoredItems = storage.StorageLostAndFound.GetStorageItemList();
        List<ItemBase> available = restoredItems
            .Where(item => item != null && !registry.TryGet(
                NetworkedItem.TryGetNetworkedItem(item, out NetworkedItem existing) ? existing.NetId : (ushort)0,
                out _)).ToList();

        foreach (JObject value in pendingSaveImports.ToArray())
        {
            if (!Guid.TryParse((string)value["persistentItemId"], out Guid persistentItemId) ||
                persistentItemId == Guid.Empty)
            {
                // Clean-break format: do not infer persistent identity from a transient NetId.
                pendingSaveImports.Remove(value);
                Multiplayer.LogWarning("Ignoring Lost and Found save record without persistentItemId");
                continue;
            }
            string ownerIdentity = (string)value["ownerIdentity"] ?? string.Empty;
            if (registry.TryGetByPersistentId(persistentItemId, out _))
            {
                pendingSaveImports.Remove(value);
                Multiplayer.LogWarning($"Ignoring duplicate Lost and Found persistentItemId {persistentItemId:D}");
                continue;
            }
            string prefab = (string)value["prefabName"] ?? string.Empty;
            int storageIndex = (int?)value["storageIndex"] ?? -1;
            if (storageIndex < 0)
            {
                // Saved after its item was destroyed (older builds kept such entries). Nothing of
                // it is in storage, and the same-prefab fallback below would bind it to another
                // item, such as a live booklet of another job.
                pendingSaveImports.Remove(value);
                Multiplayer.LogWarning($"Dropping Lost and Found save record {persistentItemId:D} " +
                    $"({prefab}) with no stored item");
                continue;
            }
            if (!Guid.TryParse(ownerIdentity, out Guid ownerId) || ownerId == Guid.Empty)
            {
                pendingSaveImports.Remove(value);
                Multiplayer.LogWarning($"Dropping Lost and Found save record {persistentItemId:D} ({prefab}) with no owner");
                continue;
            }
            // Bound at load whether its owner is here or not: an offline owner's entry is theirs under
            // the absent owner number, rebound to their session's when they join (owned-items PR 2a).
            byte ownerNumber = OwnedItemsServer.OwnerNumber(ownerId);
            string ownerName = ItemChangeLog.PlayerName(ownerNumber) ?? ownerIdentity;
            ItemBase baseItem = FindStoredItem(restoredItems, available, persistentItemId, storageIndex, prefab);
            if (baseItem == null)
            {
                // DV's Lost and Found storage, loaded with the save, has nothing left for this
                // entry, and it won't gain anything later. Kept, it showed nowhere but still
                // counted as the owner's item: DV made them no new radio and /give refused one.
                pendingSaveImports.Remove(value);
                Multiplayer.LogWarning($"Lost and Found: {ownerName}'s saved {prefab} ({persistentItemId:D}) " +
                                       "isn't in the game's Lost and Found storage; dropping the entry");
                continue;
            }
            if (baseItem == null || !NetworkedItem.TryGetNetworkedItem(baseItem, out NetworkedItem item) || item.NetId == 0)
                continue;
            string authoredItemKey = (string)value["authoredItemKey"] ?? string.Empty;
            if (WorldItemStableIdentity.IsValid(authoredItemKey))
                item.AdoptAuthoredItemKey(authoredItemKey);
            available.Remove(baseItem);
            uint revision = (uint?)value["revision"] ?? 0;
            int claimSlot = (int?)value["inventoryClaimSlot"] ?? -1;
            // The claim is the owner's: their number now, not the one saved with the entry.
            byte claimPlayerId = claimSlot >= 0 && ownerNumber != ItemTransitionCommand.AbsentOwnerId ? ownerNumber : (byte)0;
            ItemInventoryClaimFlags claimFlags = (ItemInventoryClaimFlags)((byte?)value["inventoryClaimFlags"] ?? 0);
            AuthoritativeItemRegistry.RestoreLostRecord(item, ownerNumber, revision, prefab,
                claimPlayerId, claimSlot, claimFlags, ItemMoveReason.RestoredFromSave, ownerId);
            LostItemRecord restored = new()
            {
                PersistentItemId = persistentItemId,
                NetId = item.NetId,
                Revision = revision,
                OwnerPlayerId = ownerNumber,
                OwnerIdentity = ownerIdentity,
                PrefabName = prefab,
                DisplayName = (string)value["displayName"] ?? baseItem.name,
                AuthoredItemKey = authoredItemKey,
                InventoryClaimPlayerId = claimPlayerId,
                InventoryClaimSlot = claimSlot,
                InventoryClaimFlags = (byte)claimFlags,
                Reason = (LostItemReason)((byte?)value["reason"] ?? 0),
                LostUtcTicks = (long?)value["lostUtcTicks"] ?? DateTime.UtcNow.Ticks
            };
            LogRestore(registry.Restore(restored), persistentItemId, "save import");
            pendingSaveImports.Remove(value);
        }
        if (registry.Count > countBefore)
            generation++;
    }

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

    private static bool HasSettledPosition(ServerPlayer player, float now) =>
        NetworkLifecycle.Instance.IsHost(player) || player.TrackingData.Position.HasValue &&
        player.LoadingState == PlayerLoadingState.Complete && player.CompletedLoadingAt >= 0f &&
        now - player.CompletedLoadingAt >= Multiplayer.Settings.LostItemCollectionGraceSeconds;

    private static LostItemCandidate BuildCandidate(NetworkedItem item,
        AuthoritativeItemRegistry.Record authority, float now)
    {
        // A just-joined owner's items were collected seconds after they loaded (ledger-save on a
        // dedicated host, 2026-10-21): before their game sends a position it reads as the origin, and
        // its first ones can be from before it put the player at their saved spot. An owner's
        // position counts once they have been loaded for a grace period.
        bool ownerKnown = NetworkServer.Instance.TryGetServerPlayer(
            authority.PersistentOwnerPlayerId, out ServerPlayer owner) && HasSettledPosition(owner, now);
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
            PersistentOwnerPlayerId = authority.PersistentOwnerPlayerId,
            Placement = (AuthorityPlacement)(byte)authority.Placement,
            IsGrabbed = item.Item?.IsGrabbed() == true,
            IsSnapped = item.Item?.SnappableItem?.SnappedTo != null,
            IsInMachine = contained,
            IsLicensePaper = IsLicensePaper(item),
            IsPersonalItem = item.Item?.InventorySpecs?.BelongsToPlayer == true,
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
            AuthorityPlacement? placement = AuthoritativeItemRegistry.TryGet(lost.NetId,
                out AuthoritativeItemRegistry.Record authority)
                ? (AuthorityPlacement)(byte)authority.Placement : null;
            switch (LostItemEntryReconciler.Classify(itemPresent, placement))
            {
                case LostItemEntryStatus.Stored:
                    // Entries restored from a save bound to items the load left active where they
                    // were dropped: in Lost and Found and lying in the world at once. Keep them inert.
                    if (item.gameObject.activeSelf)
                        ProjectToInertStorage(item);
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

    private static void RemoveStaleEntry(ushort itemNetId, string reason)
    {
        if (registry.RemoveStale(itemNetId, out LostItemRecord removed) !=
            LostItemRegistryResult.Removed)
            return;
        generation++;
        eligibleSince.Remove(itemNetId);
        Multiplayer.Log($"[LostAndFound] Removed entry handle={removed.Handle}, netId={itemNetId}, " +
            $"prefab={removed.PrefabName}, owner=P{removed.OwnerPlayerId}: {reason}");
        if (NetworkServer.Instance.TryGetServerPlayer(removed.OwnerPlayerId,
                out ServerPlayer owner))
            NetworkServer.Instance.SendLostItemsSnapshot(owner);
        Publish("item.lost-and-found-entry-invalidated", itemNetId, new()
        {
            ["ownerPlayerId"] = removed.OwnerPlayerId,
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

    public static void OnOwnerConnected(ServerPlayer player)
    {
        if (player == null || player.Guid == Guid.Empty)
            return;
        if (registry.RebindOwner(player.Guid.ToString("D"), player.PlayerId) > 0)
            generation++;
    }

    /// <summary>Whether a Lost and Found entry, restored or not yet, belongs to this player ID.</summary>
    internal static bool HasOwner(Guid owner) =>
        registry.HasOwner(owner.ToString("D")) || PendingOwnedBy(owner).Any();

    /// <summary>A player's ID changed (InstallGuidRekey): their entries follow. Returns how many changed.</summary>
    internal static int RekeyOwner(Guid from, Guid to)
    {
        int changed = registry.RekeyOwner(from.ToString("D"), to.ToString("D"));
        foreach (JObject value in PendingOwnedBy(from).ToArray())
        {
            value["ownerIdentity"] = to.ToString("D");
            changed++;
        }
        if (changed > 0)
            generation++;
        return changed;
    }

    private static IEnumerable<JObject> PendingOwnedBy(Guid owner) =>
        pendingSaveImports.Where(value => Guid.TryParse((string)value["ownerIdentity"], out Guid identity) && identity == owner);

    public static void OnOwnerDisconnected(ServerPlayer player)
    {
        if (player != null && registry.UnbindOwner(player.PlayerId) > 0)
            generation++;
    }

    private static string OwnerIdentity(byte id) =>
        NetworkServer.Instance.TryGetServerPlayer(id, out ServerPlayer player)
            ? player.Guid.ToString("D") : string.Empty;

    private static LostItemData ToData(LostItemRecord record) => new()
    {
        Handle = record.Handle,
        NetId = record.NetId,
        Revision = record.Revision,
        PrefabName = record.PrefabName,
        DisplayName = record.DisplayName,
        Reason = (byte)record.Reason,
        LostUtcTicks = record.LostUtcTicks
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
        entry["ownerPlayerId"] = record.OwnerPlayerId;
        entry["ownerIdentity"] = record.OwnerIdentity;
        entry["prefabName"] = record.PrefabName;
        entry["displayName"] = record.DisplayName;
        entry["authoredItemKey"] = record.AuthoredItemKey ?? string.Empty;
        entry["reason"] = record.Reason.ToString();
        entry["lostUtc"] = FormatUtc(record.LostUtcTicks);
        entry["inventoryClaimPlayerId"] = record.InventoryClaimPlayerId;
        entry["inventoryClaimSlot"] = record.InventoryClaimSlot;
        entry["inventoryClaimFlagsRaw"] = record.InventoryClaimFlags;
        entry["canonicalUnityObjectPresent"] = item != null;
        if (AuthoritativeItemRegistry.TryGet(record.NetId, out AuthoritativeItemRegistry.Record authority))
        {
            entry["authorityPlacement"] = authority.Placement.ToString();
            entry["authorityRevision"] = authority.Revision;
            entry["authorityPlacementPlayerId"] = authority.PlacementPlayerId;
            entry["authorityTransitionReason"] = authority.LastReason.ToString();
        }
        return entry;
    }

    private static void RecordEvaluation(NetworkedItem item,
        AuthoritativeItemRegistry.Record authority, LostItemDecision decision,
        LostItemCandidate candidate)
    {
        Dictionary<string, object> entry = new(StringComparer.Ordinal)
        {
            ["netId"] = item.NetId,
            ["displayName"] = item.Item?.name ?? authority.PrefabName,
            ["prefabName"] = authority.PrefabName,
            ["revision"] = authority.Revision,
            ["ownerPlayerId"] = authority.PersistentOwnerPlayerId,
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

    private static JObject Serialize(LostItemRecord record)
    {
        int storageIndex = -1;
        if (NetworkedItem.TryGet(record.NetId, out NetworkedItem item) && item?.Item != null)
            storageIndex = StorageController.Instance?.StorageLostAndFound?.GetStorageItemList()
                .IndexOf(item.Item) ?? -1;
        return new JObject
        {
            ["persistentItemId"] = record.PersistentItemId.ToString("D"),
            ["storageIndex"] = storageIndex,
            ["revision"] = record.Revision,
            ["ownerIdentity"] = record.OwnerIdentity,
            ["prefabName"] = record.PrefabName,
            ["displayName"] = record.DisplayName,
            ["authoredItemKey"] = record.AuthoredItemKey ?? string.Empty,
            ["inventoryClaimPlayerId"] = record.InventoryClaimPlayerId,
            ["inventoryClaimSlot"] = record.InventoryClaimSlot,
            ["inventoryClaimFlags"] = record.InventoryClaimFlags,
            ["reason"] = (byte)record.Reason,
            ["lostUtcTicks"] = record.LostUtcTicks
        };
    }

    private static void TraceDecision(NetworkedItem item, AuthoritativeItemRegistry.Record authority,
        LostItemDecision decision, LostItemCandidate candidate)
    {
        if (!DebugRuntime.EnabledFor("inventory")) return;
        Publish("item.lost-and-found-evaluated", item, new()
        {
            ["decision"] = decision.Kind.ToString(),
            ["reason"] = decision.Reason,
            ["ownerPlayerId"] = authority.PersistentOwnerPlayerId,
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
