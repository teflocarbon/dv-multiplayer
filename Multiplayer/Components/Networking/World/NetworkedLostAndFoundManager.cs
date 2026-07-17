using DV.CabControls;
using DV.InventorySystem;
using DV.JObjectExtstensions;
using Multiplayer.Core.Items;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Integrations.Inventory;
using Multiplayer.Integrations.Storage;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Components.Networking.World;

/// <summary>Host-authoritative virtual Lost and Found. Vanilla storage is an inert projection.</summary>
public static class NetworkedLostAndFoundManager
{
    private const uint ScanIntervalTicks = 24;
    private static readonly LostItemRegistry registry = new();
    private static readonly Dictionary<ushort, float> eligibleSince = new();
    private static readonly List<LostItemData> clientItems = new();
    private static readonly List<JObject> pendingSaveImports = new();
    private static readonly Dictionary<ushort, Dictionary<string, object>> lastEvaluations = new();
    private static uint generation;
    private static bool saveImportAttempted;

    public static event Action ClientListChanged;
    public static IReadOnlyList<LostItemData> ClientItems => clientItems;
    public static uint Generation => generation;
    public static bool Contains(ushort itemNetId) => registry.TryGet(itemNetId, out _);

#if DEBUG
    internal static void RemoveRuntimeFixture(ushort itemNetId)
    {
        eligibleSince.Remove(itemNetId);
        lastEvaluations.Remove(itemNetId);
        if (registry.RemoveStale(itemNetId, out LostItemRecord removed) ==
            LostItemRegistryResult.Removed)
        {
            generation++;
            if (NetworkLifecycle.Instance?.IsHost() == true &&
                NetworkLifecycle.Instance.Server.TryGetServerPlayer(
                    removed.OwnerPlayerId, out ServerPlayer owner))
                NetworkLifecycle.Instance.Server.SendLostItemsSnapshot(owner);
        }
        if (clientItems.RemoveAll(item => item.NetId == itemNetId) > 0)
            ClientListChanged?.Invoke();
    }
#endif

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
        return true;
    }

    public static void HostTick(uint tick)
    {
        if (!NetworkLifecycle.Instance.IsHost() || tick % ScanIntervalTicks != 0)
            return;
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
            rejectionReason = "missing-canonical-unity-item";
            TraceRecallRejection(itemNetId, rejectionReason);
            return false;
        }
        if (!AuthoritativeItemRegistry.TryGet(itemNetId, out AuthoritativeItemRegistry.Record authority) ||
            authority.Placement != ItemPlacementKind.LostAndFound)
        {
            RemoveStaleEntry(itemNetId, item, "canonical-item-no-longer-in-lost-and-found");
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
            registry.Restore(removedRecord);
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
        generation++;
        if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(player.PlayerId,
                out ServerPlayer owner))
            NetworkLifecycle.Instance.Server.SendLostItemsSnapshot(owner);
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

    public static bool Collect(NetworkedItem item, LostItemReason reason)
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
        if (!AuthoritativeItemRegistry.TryMoveToLostAndFound(item,
                out AuthoritativeItemRegistry.Record authority, out string rejectionReason,
                allowRecoveryPlacement: reason != LostItemReason.OwnerDistance))
        {
            Publish("item.lost-and-found-collection-rejected", item,
                new() { ["rejectionReason"] = rejectionReason }, DebugSeverity.Warning);
            return false;
        }
        LostItemRecord record = new()
        {
            PersistentItemId = Guid.NewGuid(),
            NetId = item.NetId,
            Revision = authority.Revision,
            OwnerPlayerId = authority.PersistentOwnerPlayerId,
            OwnerIdentity = OwnerIdentity(authority.PersistentOwnerPlayerId),
            PrefabName = authority.PrefabName,
            DisplayName = item.Item?.name ?? authority.PrefabName,
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
        ItemUpdateData destroy = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Destroy);
        if (destroy != null)
            AuthoritativeItemRegistry.WriteToSnapshot(authority, destroy,
                ItemTransitionReason.LostAndFoundCollection);
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
        foreach (ServerPlayer player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            player.NearbyItems.Remove(item);
            player.KnownItems.Remove(item);
        }
        if (destroy != null)
            NetworkLifecycle.Instance.Server.SendItemUpdatePacket(destroy);
        if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(authority.PersistentOwnerPlayerId,
                out ServerPlayer owner))
            NetworkLifecycle.Instance.Server.SendLostItemsSnapshot(owner);
        Publish("item.lost-and-found-collected", item, new()
        {
            ["ownerPlayerId"] = authority.PersistentOwnerPlayerId,
            ["reason"] = reason.ToString(),
            ["generation"] = generation
        });
        return true;
    }

    public static void ApplyClientSnapshot(uint newGeneration, LostItemData[] items)
    {
        generation = newGeneration;
        clientItems.Clear();
        if (items != null)
            clientItems.AddRange(items.Where(item => item != null));
        ClientListChanged?.Invoke();
    }

    public static void Clear()
    {
        registry.Clear();
        eligibleSince.Clear();
        clientItems.Clear();
        pendingSaveImports.Clear();
        lastEvaluations.Clear();
        generation = 0;
        saveImportAttempted = false;
        ClientListChanged?.Invoke();
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
        if (NetworkLifecycle.Instance?.IsClientRunning == true)
        {
            foreach (LostItemData item in clientItems)
            {
                result.Add(new Dictionary<string, object>
                {
                    ["debugKey"] = $"client-handle:{item.Handle}",
                    ["source"] = "client-authoritative-list",
                    ["status"] = "ListedByHost",
                    ["lostHandle"] = item.Handle,
                    ["netId"] = item.NetId,
                    ["revision"] = item.Revision,
                    ["prefabName"] = item.PrefabName ?? string.Empty,
                    ["displayName"] = item.DisplayName ?? string.Empty,
                    ["ownerPlayerId"] = DebugRuntime.Session?.PlayerId ?? 0,
                    ["reason"] = ((LostItemReason)item.Reason).ToString(),
                    ["lostUtc"] = FormatUtc(item.LostUtcTicks)
                });
            }
        }
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
        TryBindPendingSaveEntries();
    }

    private static void TryBindPendingSaveEntries()
    {
        if (pendingSaveImports.Count == 0 || NetworkLifecycle.Instance?.Server == null)
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
            ServerPlayer owner = NetworkLifecycle.Instance.Server.ServerPlayers.FirstOrDefault(player =>
                string.Equals(player.Guid.ToString("D"), ownerIdentity, StringComparison.OrdinalIgnoreCase));
            if (owner == null) continue;
            string prefab = (string)value["prefabName"] ?? string.Empty;
            int storageIndex = (int?)value["storageIndex"] ?? -1;
            ItemBase baseItem = storageIndex >= 0 && storageIndex < restoredItems.Count
                ? restoredItems[storageIndex]
                : null;
            if (baseItem == null || !available.Contains(baseItem) ||
                !string.Equals(baseItem.InventorySpecs?.ItemPrefabName, prefab, StringComparison.Ordinal))
                continue;
            if (baseItem == null || !NetworkedItem.TryGetNetworkedItem(baseItem, out NetworkedItem item) || item.NetId == 0)
                continue;
            available.Remove(baseItem);
            uint revision = (uint?)value["revision"] ?? 0;
            byte claimPlayerId = (byte?)value["inventoryClaimPlayerId"] ?? 0;
            int claimSlot = (int?)value["inventoryClaimSlot"] ?? -1;
            ItemInventoryClaimFlags claimFlags = (ItemInventoryClaimFlags)((byte?)value["inventoryClaimFlags"] ?? 0);
            AuthoritativeItemRegistry.RestoreLostRecord(item, owner.PlayerId, revision, prefab,
                claimPlayerId, claimSlot, claimFlags);
            registry.Restore(new LostItemRecord
            {
                PersistentItemId = persistentItemId,
                NetId = item.NetId,
                Revision = revision,
                OwnerPlayerId = owner.PlayerId,
                OwnerIdentity = ownerIdentity,
                PrefabName = prefab,
                DisplayName = (string)value["displayName"] ?? baseItem.name,
                InventoryClaimPlayerId = claimPlayerId,
                InventoryClaimSlot = claimSlot,
                InventoryClaimFlags = (byte)claimFlags,
                Reason = (LostItemReason)((byte?)value["reason"] ?? 0),
                LostUtcTicks = (long?)value["lostUtcTicks"] ?? DateTime.UtcNow.Ticks
            });
            pendingSaveImports.Remove(value);
        }
        if (registry.Count > countBefore)
            generation++;
    }

    private static LostItemDecision Evaluate(LostItemCandidate candidate) =>
        LostItemCollectionPolicy.Evaluate(candidate, Multiplayer.Settings.LostItemOwnerDistance,
            Multiplayer.Settings.LostItemNearbyPlayerProtectionDistance,
            Multiplayer.Settings.LostItemCollectionGraceSeconds);

    private static LostItemCandidate BuildCandidate(NetworkedItem item,
        AuthoritativeItemRegistry.Record authority, float now)
    {
        bool ownerKnown = NetworkLifecycle.Instance.Server.TryGetServerPlayer(
            authority.PersistentOwnerPlayerId, out ServerPlayer owner);
        List<ServerPlayer> players = NetworkLifecycle.Instance.Server.ServerPlayers
            .Where(player => player != null && player.LoadingState >= PlayerLoadingState.ReadyForItems)
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
            if (!AuthoritativeItemRegistry.TryGet(lost.NetId,
                    out AuthoritativeItemRegistry.Record authority) ||
                authority.Placement == ItemPlacementKind.LostAndFound)
                continue;
            NetworkedItem.TryGet(lost.NetId, out NetworkedItem item);
            RemoveStaleEntry(lost.NetId, item, "canonical-placement-changed");
        }
    }

    private static void RemoveStaleEntry(ushort itemNetId, NetworkedItem item, string reason)
    {
        if (registry.RemoveStale(itemNetId, out LostItemRecord removed) !=
            LostItemRegistryResult.Removed)
            return;
        generation++;
        eligibleSince.Remove(itemNetId);
        if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(removed.OwnerPlayerId,
                out ServerPlayer owner))
            NetworkLifecycle.Instance.Server.SendLostItemsSnapshot(owner);
        if (item != null)
            Publish("item.lost-and-found-entry-invalidated", item, new()
            {
                ["ownerPlayerId"] = removed.OwnerPlayerId,
                ["reason"] = reason,
                ["generation"] = generation
            }, DebugSeverity.Warning);
    }

    private static void TraceRecallRejection(ushort itemNetId, string reason, uint lostHandle = 0)
    {
        global::Multiplayer.Multiplayer.LogWarning($"[LostAndFound Recall] Manager retrieval rejected: " +
            $"handle={lostHandle}, netId={itemNetId}, reason={reason ?? string.Empty}");
    }

    private static string OwnerIdentity(byte id) =>
        NetworkLifecycle.Instance.Server.TryGetServerPlayer(id, out ServerPlayer player)
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
        Dictionary<string, object> data, DebugSeverity severity = DebugSeverity.Info)
    {
        if (!DebugRuntime.EnabledFor("inventory")) return;
        DebugRuntime.Publish("inventory", eventName, DebugRuntimeSide.Server, severity,
            "Item", item?.NetId.ToString() ?? "0", data);
    }
}
