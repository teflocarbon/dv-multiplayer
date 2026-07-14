using System.Collections.Generic;
using System.Linq;
using DV.Utils;
using UnityEngine;
using JetBrains.Annotations;
using Multiplayer.Networking.Data;
using Multiplayer.Components.Networking.World;
using System;
using Multiplayer.Utils;
using DV;
using DV.InventorySystem;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Components.Networking.World;

public class NetworkedItemManager : SingletonBehaviour<NetworkedItemManager>
{
    public const float TrackedValueFinalizationGraceSeconds = 0.75f;
    /*
     * Server 
     */

    //Culling distance for items
    public const float MAX_DISTANCE_TO_ITEM = 100f;
    public const float MAX_DISTANCE_TO_ITEM_SQR = MAX_DISTANCE_TO_ITEM * MAX_DISTANCE_TO_ITEM;
    public const float NEARBY_REMOVAL_DELAY = 3f; // 3 seconds delay

    //caches for item snapshots
    private readonly List<ItemUpdateData> DestroyedItems = new(64);

    /*
     * Client
     */

    //cache for client-sided items & spawns
    private readonly Dictionary<string, List<NetworkedItem>> CachedItems = new(1024); //Client cached items
    private readonly HashSet<NetworkedItem> CachedItemSet = new();
    private readonly Queue<NetworkedItem> PendingUnboundClientItems = new();
    private readonly HashSet<NetworkedItem> PendingUnboundClientItemSet = new();
    private readonly Dictionary<string, InventoryItemSpec> ItemPrefabs = new(1024);   //Item prefabs
    private readonly Queue<NetworkedItem> PendingLocalStateObservations = new();
    private readonly HashSet<NetworkedItem> PendingLocalStateObservationSet = new();
    private readonly Dictionary<NetworkedItem, string> PendingLocalStateReasons = new();
    private readonly Dictionary<string, NetworkedItem> PendingItemAdoptions = new(StringComparer.Ordinal);
    private readonly Dictionary<NetworkedItem, string> PendingItemAdoptionTokens = new();
    private readonly Dictionary<NetworkedItem, float> PendingTrackedValueFinalizations = new();
    private bool ClientInitialised = false;


    /* 
     * Common
     */

    protected override void Awake()
    {
        base.Awake();
    }

    protected void Start()
    {
        if (NetworkLifecycle.Instance.IsHost())
            NetworkLifecycle.Instance.OnTick += Common_OnTick;

        BuildPrefabLookup();
    }

    protected void Update()
    {
        ProcessTrackedValueFinalizations();
        ProcessPendingLocalStateObservations();
        if (NetworkLifecycle.Instance.IsHost())
            return;

        if (!ClientInitialised)
            return;

        int remainingBudget = 16;
        while (remainingBudget-- > 0 && PendingUnboundClientItems.Count > 0)
        {
            NetworkedItem item = PendingUnboundClientItems.Dequeue();
            PendingUnboundClientItemSet.Remove(item);
            if (item == null || item.NetId != 0)
                continue;

            if (!CachedItemSet.Contains(item) && CanCacheClientSceneItem(item))
            {
                TraceItem("item.unbound-scene-item-cached", item, new() { ["reason"] = "late-loaded-or-missed-initial-cache" });
                SendToCache(item);
            }
            else
            {
                item.AllowUnboundLocalInteraction();
            }
        }
    }

    internal void QueueLocalStateObservation(NetworkedItem item, string reason)
    {
        if (item == null)
            return;
        PendingLocalStateReasons[item] = reason ?? string.Empty;
        if (!PendingLocalStateObservationSet.Add(item))
            return;
        PendingLocalStateObservations.Enqueue(item);
    }

    internal void QueueInventoryStateObservations(string reason)
    {
        foreach (NetworkedItem item in NetworkedItem.GetAll().Where(item => item != null).Distinct())
        {
            bool stateChanged = item.DebugCurrentState != item.DebugLastState;
            // InventoryStatusChanged is broad and may fire repeatedly for the entire
            // inventory. Membership alone does not mean an item changed. Queue only real
            // state transitions; otherwise every status notification produces unchanged /
            // suppressed events for every carried item and floods the replication UI.
            if (stateChanged)
                QueueLocalStateObservation(item, reason);
        }
    }

    private void ProcessPendingLocalStateObservations()
    {
        int budget = 32;
        while (budget-- > 0 && PendingLocalStateObservations.Count > 0)
        {
            NetworkedItem item = PendingLocalStateObservations.Dequeue();
            PendingLocalStateObservationSet.Remove(item);
            PendingLocalStateReasons.TryGetValue(item, out string reason);
            PendingLocalStateReasons.Remove(item);
            if (item == null)
                continue;
            item.ProcessLocalStateObservation(string.IsNullOrEmpty(reason) ? "queued-state-hook" : reason);
        }
    }

    internal void ScheduleTrackedValueFinalization(NetworkedItem item)
    {
        if (item == null || item.TrackedValuesFinalised || PendingTrackedValueFinalizations.ContainsKey(item))
            return;
        PendingTrackedValueFinalizations[item] = Time.realtimeSinceStartup + TrackedValueFinalizationGraceSeconds;
    }

    private void ProcessTrackedValueFinalizations()
    {
        if (PendingTrackedValueFinalizations.Count == 0)
            return;

        float now = Time.realtimeSinceStartup;
        foreach (NetworkedItem item in PendingTrackedValueFinalizations.Keys.ToArray())
        {
            if (item == null || item.TrackedValuesFinalised)
            {
                PendingTrackedValueFinalizations.Remove(item);
                continue;
            }
            if (now < PendingTrackedValueFinalizations[item])
                continue;

            PendingTrackedValueFinalizations.Remove(item);
            item.FinaliseTrackedValuesAutomatically();
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (UnloadWatcher.isQuitting)
            return;

        if (NetworkLifecycle.Instance.IsHost())
            NetworkLifecycle.Instance.OnTick -= Common_OnTick;
    }

    public void AddDirtyItemSnapshot(NetworkedItem netItem, ItemUpdateData snapshot)
    {
        DestroyedItems.Add(snapshot);

        foreach (var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.KnownItems.ContainsKey(netItem))
                player.KnownItems.Remove(netItem);

            if (player.NearbyItems.ContainsKey(netItem))
                player.NearbyItems.Remove(netItem);
        }
    }

    //public void ReceiveSnapshots(List<ItemUpdateData> snapshots)
    //{
    //    if (snapshots == null)
    //        return;

    //    foreach (var snapshot in snapshots)
    //        ReceivedSnapshots.Enqueue(snapshot);

    //    Multiplayer.LogDebug(() => $"NetworkItemManager.ReceiveSnapshots() count: {ReceivedSnapshots.Count}, from: ");
    //}

    #region Common

    private void Common_OnTick(uint tick)
    {
        //ProcessReceived();

        if (NetworkLifecycle.Instance.IsHost())
        {
            UpdatePlayerItemLists();
            ProcessChanged(tick);
        }
        else
        {
            //ProcessClientChanges(tick);
        }
    }

    public void ReceiveSnapshots(List<ItemUpdateData> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            //var snapshot = ReceivedSnapshots.Dequeue();
            try
            {
                //Multiplayer.LogDebug(() => $"ProcessReceived: {snapshot.UpdateType}");

                if (snapshot == null || snapshot.UpdateType == ItemUpdateData.ItemUpdateType.None)
                {
                    Multiplayer.LogError($"NetworkedItemManager.ProcessReceived() Invalid Update Type: {snapshot?.UpdateType}, ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
                    continue;
                }

                //if (NetworkLifecycle.Instance.IsHost())
                //{
                //    NetworkLifecycle.Instance.Server.TryGetServerPlayer(snapshot.Player, out ServerPlayer player);
                //    ProcessReceivedAsHost(snapshot, player);
                //}
                //else
                //{
                    ProcessReceivedAsClient(snapshot);
                //}
            }
            catch (Exception ex)
            {
                Multiplayer.LogError($"NetworkedItemManager.ProcessReceived() Error! {ex.Message}\r\n{ex.StackTrace}");
            }
        }
    }

    #endregion

    #region Server

    private void UpdatePlayerItemLists()
    {
        float currentTime = Time.time;

        var allItems = NetworkedItem.GetAll();

        foreach (var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;

            foreach (var item in allItems)
            {
                if (item == null)
                    continue;

                float sqrDistance = (player.WorldPosition - item.transform.position).sqrMagnitude;

                if (sqrDistance <= MAX_DISTANCE_TO_ITEM_SQR)
                {
                    bool enteredRelevance = !player.NearbyItems.ContainsKey(item);
                    //NetworkLifecycle.Instance.Server.LogDebug(() => $"UpdatePlayerItemLists() Adding for player: {player?.Username}, Nearby Item: {item?.NetId}, {item?.name}");
                    player.NearbyItems[item] = currentTime;
                    if (enteredRelevance) TraceItem("item.relevance-enter", item, new() { ["playerId"] = player.PlayerId, ["distance"] = Mathf.Sqrt(sqrDistance) });
                }
            }

            // Remove items that are no longer nearby
            for (int i = 0; i < player.NearbyItems.Count; i++)
            {
                var kvp = player.NearbyItems.ElementAt(i);

                if (currentTime - kvp.Value > NEARBY_REMOVAL_DELAY)
                {
                    //NetworkLifecycle.Instance.Server.LogDebug(() => $"UpdatePlayerItemLists() Removing for player: {player?.Username}, Nearby Item: {kvp.Key?.NetId}, {kvp.Key?.name}");
                    player.NearbyItems.Remove(kvp.Key);
                    TraceItem("item.relevance-leave", kvp.Key, new() { ["playerId"] = player.PlayerId });
                }
            }
        }
    }

    private void ProcessChanged(uint tick)
    {
        List<ItemUpdateData> dirtyItems = [];
        float timeStamp = Time.time;
        NetworkLifecycle.Instance.Server.TryGetServerPlayer(NetworkLifecycle.Instance.Server.SelfId, out ServerPlayer hostPlayer);

        foreach (var item in NetworkedItem.GetAll())
        {
            if (item == null)
                continue;
            ItemUpdateData snapshot = item.GetSnapshot();
            if (snapshot != null && hostPlayer != null)
            {
                if (!AuthoritativeItemRegistry.TryApplyTransition(item, snapshot, hostPlayer,
                        ItemTransitionReason.HostLocalState, true, out string rejectionReason))
                {
                    DebugTrace.Validation("item", "Item", item.NetId.ToString(), false,
                        rejectionReason, DebugRuntimeSide.Server);
                    continue;
                }
                item.ApplyHostLocalCanonicalTransition(snapshot, hostPlayer);
                dirtyItems.Add(snapshot);
            }
        }

        //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) DirtyItems: {dirtyItems.Count}");

        foreach (var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;

            if (player.Peer == NetworkLifecycle.Instance.Server.SelfPeer)
                continue;

            List<ItemUpdateData> playerUpdates = [];

            // Process nearby items
            foreach (var nearbyItem in player.NearbyItems.Keys)
            {
                if (!player.KnownItems.ContainsKey(nearbyItem))
                {
                    // This is a new item for the player
                    //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) New item for: {player.Username}, itemNetID{nearbyItem.NetId}");

                    ItemUpdateData snapshot = nearbyItem.CreateUpdateData(ItemUpdateData.ItemUpdateType.Create);
                    player.KnownItems[nearbyItem] = tick;

                    //prevent propagation of creates for special items
                    if (!DoNotCreateItem(nearbyItem))
                        playerUpdates.Add(snapshot);
                    else
                        TraceItem("item.generic-create-suppressed", nearbyItem, new()
                        {
                            ["trackedItemType"] = nearbyItem.TrackedItemType?.FullName ?? string.Empty,
                            ["playerId"] = player.PlayerId
                        });
                }
                else
                {
                    // Check if this item is in the dirty items list
                    var dirtyUpdate = dirtyItems.FirstOrDefault(di => di.ItemNetId == nearbyItem.NetId);

                    //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Item exists for: {player.Username}, {dirtyUpdate != null}");

                    if (dirtyUpdate == null)
                    {
                        //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Item exists for: {player.Username}, LastDirtyTick: {player.KnownItems[nearbyItem] < nearbyItem.LastDirtyTick}");
                        if (player.KnownItems[nearbyItem] < nearbyItem.LastDirtyTick)
                        {
                            dirtyUpdate = nearbyItem.CreateUpdateData(ItemUpdateData.ItemUpdateType.FullSync);
                        }
                    }

                    if (dirtyUpdate != null)
                    {
                        Multiplayer.LogDebug(() => $"ProcessChanged({tick}) Update Type: {dirtyUpdate.UpdateType}, Item State: {dirtyUpdate.ItemState}");
                        playerUpdates.Add(dirtyUpdate);
                        player.KnownItems[nearbyItem] = tick;
                    }
                }
            }

            //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Adding {DestroyedItems.Count()} DestroyedItems for: {player.Username}");

            playerUpdates.AddRange(DestroyedItems);

            if (playerUpdates.Count > 0)
            {
                //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Sending {playerUpdates.Count()} to player: {player.Username}");
                NetworkLifecycle.Instance.Server.SendItemsBulkUpdatePacket(playerUpdates, player);
            }
        }

        DestroyedItems.Clear();
    }

    #endregion

    #region Client

    //private void ProcessClientChanges(uint tick)
    //{
    //    List<ItemUpdateData> changedItems = new List<ItemUpdateData>();

    //    if(!ClientInitialised)
    //        return;

    //    foreach (var item in NetworkedItem.GetAll())
    //    {
    //        ItemUpdateData snapshot = item.GetSnapshot();
    //        if (snapshot != null)
    //        {
    //            changedItems.Add(snapshot);
    //        }
    //    }

    //    if (changedItems.Count > 0)
    //    {
    //        NetworkLifecycle.Instance.Client.SendItemsBulkUpdatePacket(changedItems);
    //    }
    //}

    private void ProcessReceivedAsClient(ItemUpdateData snapshot)
    {
        NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem netItem);
        TraceSnapshot("item.snapshot-dispatch", snapshot);

        NetworkLifecycle.Instance.Client.LogDebug(() => $"NetworkedItemManager.ProcessReceivedAsClient() Update Type: {snapshot?.UpdateType}, ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
        if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Create)
        {
            // Job-created documents already own their authoritative ID. Applying the generic
            // snapshot to that representation is valid; replacing it would create a duplicate.
            if (netItem != null && DoNotCreateItem(netItem))
            {
                TraceItem("item.generic-create-bound-existing", netItem, new()
                {
                    ["trackedItemType"] = netItem.TrackedItemType?.FullName ?? string.Empty,
                    ["prefabName"] = snapshot.PrefabName ?? string.Empty
                });
                netItem.ReceiveSnapshot(snapshot);
                return;
            }

            //if the item already exists we need to remove it
            if (netItem != null)
                SendToCache(netItem);

            CreateItem(snapshot);
        }
        else if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Destroy)
        {
            SendToCache(netItem);
        }
        else if (netItem != null)
        {
            netItem.ReceiveSnapshot(snapshot);
        }
        else
        {
            NetworkLifecycle.Instance.Client.LogError($"NetworkedItemManager.ProcessReceivedAsClient() NetworkedItem not found on client! Update Type: {snapshot.UpdateType}, ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
            DebugRuntime.Publish("item", "item.missing-local-representation", DebugRuntimeSide.Client, DebugSeverity.Error,
                "Item", snapshot.ItemNetId.ToString(), DebugValueSnapshotter.SnapshotObject(snapshot));
        }
    }
    #endregion

    #region Item Cache And Management
    private void CreateItem(ItemUpdateData snapshot)
    {
        if (snapshot == null || snapshot.ItemNetId == 0)
        {
            Multiplayer.LogError($"NetworkedItemManager.CreateItem() Invalid snapshot! ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
            return;
        }

        NetworkedItem newItem = GetFromCache(snapshot.PrefabName);
        bool reusedFromCache = newItem != null;

        if (newItem == null)
        {
            //GameObject prefabObj = Resources.Load(snapshot.PrefabName) as GameObject;

            if (!ItemPrefabs.TryGetValue(snapshot.PrefabName, out InventoryItemSpec spec))
            {
                Multiplayer.LogError($"NetworkedItemManager.CreateItem() Unable to load prefab for ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
                return;
            }

            //create a new item
            GameObject gameObject = Instantiate(spec.gameObject, snapshot.ItemPosition + WorldMover.currentMove, snapshot.ItemRotation);

            //Make sure we have a NetworkedItem
            newItem = gameObject.GetOrAddComponent<NetworkedItem>();
            TraceItem("item.instantiated", newItem, new() { ["prefabName"] = snapshot.PrefabName });
        }

        newItem.NetId = snapshot.ItemNetId;
        newItem.SetClientNetworkBinding(true);
        newItem.gameObject.SetActive(true);
        TraceItem(reusedFromCache ? "item.cache-reused" : "item.created", newItem, DebugValueSnapshotter.SnapshotObject(snapshot));

        newItem.ReceiveSnapshot(snapshot);
    }

    private void BuildPrefabLookup()
    {
        NetworkLifecycle.Instance.Client.LogDebug(() => $"BuildPrefabLookup()");

        foreach (var item in Globals.G.Items.items)
        {
            if (!ItemPrefabs.ContainsKey(item.ItemPrefabName))
            {
                ItemPrefabs[item.itemPrefabName] = item;
            }
        }
    }
    public void CacheWorldItems()
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        // Remove all spawned world items and place them into a cache for later use
        foreach (var item in NetworkedItem.GetAll())
        {
            try
            {
                if (item == null || item.NetId != 0)
                    continue;

                ScheduleTrackedValueFinalization(item);
                if (CanCacheClientSceneItem(item))
                {
                    SendToCache(item);
                }
                else
                {
                    item.AllowUnboundLocalInteraction();
                }
                //else
                //{
                //    NetworkLifecycle.Instance.Client.LogDebug(() => $"CacheWorldItems() Not caching: {item.Item.InventorySpecs.previewPrefab} is in Inventory: {StorageController.Instance.StorageInventory.ContainsItem(item.Item)}");
                //}
            }
            catch (Exception ex)
            {
                NetworkLifecycle.Instance.Client.LogError($"Error Caching Spawned Item: {ex.Message}");
            }
        }

        ClientInitialised = true;
    }

    internal void RegisterUnboundClientItem(NetworkedItem item)
    {
        if (item == null || item.NetId != 0 || CachedItemSet.Contains(item))
            return;
        ScheduleTrackedValueFinalization(item);
        bool allowInteraction = IsPlayerOrStoredItem(item, out Dictionary<string, object> classification);
        if (allowInteraction)
            item.AllowUnboundLocalInteraction();
        else
            item.GateAsSceneObjectAwaitingHostCreate();
        classification["classification"] = allowInteraction ? "player-or-stored-item" : "scene-authored-clutter";
        TraceItem("item.unbound-classified", item, classification);
        if (PendingUnboundClientItemSet.Add(item))
            PendingUnboundClientItems.Enqueue(item);
    }

    internal void RequestItemAdoption(NetworkedItem item, string reason)
    {
        if (item == null || item.NetId != 0)
            return;
        if (PendingItemAdoptionTokens.ContainsKey(item))
            return;

        string token = Guid.NewGuid().ToString("N");
        ItemAdoptionRequestData request = item.CreateItemAdoptionRequest(token);
        if (request.Snapshot == null || string.IsNullOrWhiteSpace(request.PrefabName))
        {
            DebugRuntime.Publish("item", "item.adoption-rejected", DebugRuntimeSide.Client, DebugSeverity.Error,
                "Item", "0", new() { ["reason"] = "local-snapshot-unavailable", ["trigger"] = reason ?? string.Empty });
            return;
        }
        PendingItemAdoptions[token] = item;
        PendingItemAdoptionTokens[item] = token;
        Dictionary<string, object> data = item.LocalStateDebugData();
        data["adoptionToken"] = token;
        data["trigger"] = reason ?? string.Empty;
        data["snapshot"] = DebugTrace.ItemSnapshotData(request.Snapshot);
        DebugRuntime.Publish("item", "item.adoption-requested", DebugRuntimeSide.Client, DebugSeverity.Info,
            "Item", "0", data);
        NetworkLifecycle.Instance.Client?.SendItemAdoption(request);
    }

    public void ApplyItemAdoptionResult(ItemAdoptionResultData result)
    {
        string token = result.AdoptionToken ?? string.Empty;
        if (!PendingItemAdoptions.TryGetValue(token, out NetworkedItem item))
            return;
        PendingItemAdoptions.Remove(token);
        if (item != null)
            PendingItemAdoptionTokens.Remove(item);
        if (item == null)
            return;

        Dictionary<string, object> data = item.LocalStateDebugData();
        data["adoptionToken"] = token;
        data["assignedNetId"] = result.AssignedNetId;
        data["rejectionReason"] = result.RejectionReason ?? string.Empty;
        if (!result.Accepted || result.AssignedNetId == 0)
        {
            DebugRuntime.Publish("item", "item.adoption-rejected", DebugRuntimeSide.Client, DebugSeverity.Warning,
                "Item", "0", data);
            return;
        }

        item.CompleteItemAdoption(result);
        ScheduleTrackedValueFinalization(item);
        DebugRuntime.Publish("item", "item.adoption-complete", DebugRuntimeSide.Client, DebugSeverity.Info,
            "Item", result.AssignedNetId.ToString(), data);
    }

    public bool RequestItemRecall(NetworkedItem item, int requestedSlot)
    {
        if (item == null || item.NetId == 0)
            return false;
        byte localPlayerId = NetworkLifecycle.Instance.Client?.PlayerId ?? 0;
        DebugRuntime.Publish("item", "item.recall-requested",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: item.NetId.ToString(), data: new()
            {
                ["requestingPlayerId"] = localPlayerId,
                ["requestedSlot"] = requestedSlot,
                ["expectedRevision"] = item.AuthorityRevision,
                ["persistentOwnerPlayerId"] = item.PersistentOwnerPlayerId
            });

        if (!NetworkLifecycle.Instance.IsHost())
        {
            NetworkLifecycle.Instance.Client?.SendItemRecall(item.NetId, item.AuthorityRevision, requestedSlot);
            return true;
        }

        if (!NetworkLifecycle.Instance.Server.TryGetServerPlayer(localPlayerId, out ServerPlayer hostPlayer))
            return false;
        if (!AuthoritativeItemRegistry.TryRecall(item, hostPlayer, requestedSlot, item.AuthorityRevision,
                out ItemUpdateData snapshot, out string rejectionReason))
        {
            DebugRuntime.Publish("item", "item.recall-rejected", DebugRuntimeSide.Server,
                DebugSeverity.Warning, "Item", item.NetId.ToString(), new()
                {
                    ["requestingPlayerId"] = localPlayerId,
                    ["rejectionReason"] = rejectionReason
                });
            return false;
        }

        item.ApplyServerCanonicalSnapshot(snapshot);
        NetworkLifecycle.Instance.Server.SendItemUpdatePacket(snapshot);
        return true;
    }

    public bool TryAdoptClientItem(ServerPlayer player, ItemAdoptionRequestData request,
        out ushort assignedNetId, out string rejectionReason)
    {
        assignedNetId = 0;
        rejectionReason = string.Empty;
        if (!NetworkLifecycle.Instance.IsHost() || player == null)
        {
            rejectionReason = "invalid-runtime-or-player";
            return false;
        }
        if (string.IsNullOrWhiteSpace(request.AdoptionToken) || request.AdoptionToken.Length > 64)
        {
            rejectionReason = "invalid-adoption-token";
            return false;
        }
        if (request.Snapshot == null || request.Snapshot.ItemNetId != 0)
        {
            rejectionReason = "invalid-adoption-snapshot";
            return false;
        }
        if (!ItemPrefabs.TryGetValue(request.PrefabName ?? string.Empty, out InventoryItemSpec spec) || spec == null)
        {
            rejectionReason = "unknown-prefab";
            return false;
        }
        GameObject gameObject = Instantiate(spec.gameObject, request.Position + WorldMover.currentMove, request.Rotation);
        NetworkedItem networkedItem = gameObject.GetOrAddComponent<NetworkedItem>();
        if (networkedItem == null || networkedItem.NetId == 0)
        {
            Destroy(gameObject);
            rejectionReason = "authoritative-id-allocation-failed";
            return false;
        }

        assignedNetId = networkedItem.NetId;
        player.KnownItems[networkedItem] = NetworkLifecycle.Instance.Tick;
        ScheduleTrackedValueFinalization(networkedItem);
        request.Snapshot.ItemPosition = request.Position;
        request.Snapshot.ItemRotation = request.Rotation;
        networkedItem.ServerInitialiseAdoptedItem(player, request.Snapshot);
        return true;
    }

    internal void ResolveAuthoritativeBindingCollision(NetworkedItem incoming, ushort authoritativeId, Type trackedItemType)
    {
        if (incoming == null || authoritativeId == 0 || NetworkLifecycle.Instance.IsHost())
            return;
        if (!NetworkedItem.TryGet(authoritativeId, out NetworkedItem existing) || existing == null || existing == incoming)
            return;

        DebugRuntime.Publish("item", "item.authoritative-binding-collision", DebugRuntimeSide.Client, DebugSeverity.Error,
            "Item", authoritativeId.ToString(), new()
            {
                ["existingInstanceId"] = existing.gameObject.GetInstanceID(),
                ["existingPath"] = existing.gameObject.GetPath(),
                ["existingTrackedItemType"] = existing.TrackedItemType?.FullName ?? string.Empty,
                ["incomingInstanceId"] = incoming.gameObject.GetInstanceID(),
                ["incomingPath"] = incoming.gameObject.GetPath(),
                ["incomingTrackedItemType"] = trackedItemType?.FullName ?? string.Empty
            });

        // The job/lifecycle-created object is the authoritative representation. Retire any
        // earlier generic copy before the new object takes ownership of the ID lookup.
        SendToCache(existing);
    }

    private static bool CanCacheClientSceneItem(NetworkedItem item)
    {
        if (item?.Item == null || !item.gameObject.activeSelf || item.Item.IsEssential() || item.Item.IsGrabbed())
            return false;
        if (IsPlayerOrStoredItem(item, out _))
            return false;

        return true;
    }

    private static bool IsPlayerOrStoredItem(NetworkedItem item, out Dictionary<string, object> data)
    {
        bool belongsToPlayer = item?.Item?.InventorySpecs?.BelongsToPlayer == true;
        bool grabbed = item?.Item?.IsGrabbed() == true;
        bool inventoryMember = false;
        bool itemContainerMember = false;
        bool worldStorageMember = false;
        bool storageInventoryMember = false;
        bool storageLostAndFoundMember = false;
        bool storageItemContainerMember = false;
        bool storageAvailable = StorageController.Instance != null;
        try
        {
            inventoryMember = Inventory.Instance?.Contains(item.gameObject, false) == true;
            var container = Inventory.Instance?.ItemContainerRegistry?.GetItemContainerAndIndex(item.gameObject);
            itemContainerMember = container.HasValue && container.Value.Item1 != null;
            StorageController storage = StorageController.Instance;
            worldStorageMember = storage?.StorageWorld?.ContainsItem(item.Item) == true;
            storageInventoryMember = storage?.StorageInventory?.ContainsItem(item.Item) == true;
            storageLostAndFoundMember = storage?.StorageLostAndFound?.ContainsItem(item.Item) == true;
            storageItemContainerMember = storage?.StorageItemContainers?.ContainsItem(item.Item) == true;
        }
        catch (Exception exception)
        {
            data = new Dictionary<string, object>
            {
                ["classificationError"] = exception.Message,
                ["storageAvailable"] = storageAvailable
            };
            // Unknown is safer than briefly disabling a legitimate grab. The reconciliation
            // queue will classify it again once the storage systems are available.
            return true;
        }

        data = new Dictionary<string, object>
        {
            ["belongsToPlayer"] = belongsToPlayer,
            ["grabbed"] = grabbed,
            ["inventoryMember"] = inventoryMember,
            ["itemContainerMember"] = itemContainerMember,
            ["worldStorageMember"] = worldStorageMember,
            ["storageInventoryMember"] = storageInventoryMember,
            ["storageLostAndFoundMember"] = storageLostAndFoundMember,
            ["storageItemContainerMember"] = storageItemContainerMember,
            ["storageAvailable"] = storageAvailable
        };
        return !storageAvailable || belongsToPlayer || grabbed || inventoryMember || itemContainerMember ||
            worldStorageMember || storageInventoryMember || storageLostAndFoundMember || storageItemContainerMember;
    }

    private NetworkedItem GetFromCache(string prefabName)
    {
        if ((!CachedItems.TryGetValue(prefabName, out var cached) || cached.Count == 0) && !string.IsNullOrEmpty(prefabName))
        {
            NetworkedItem lateSceneItem = NetworkedItem.GetAll().Where(item => item != null).Distinct().FirstOrDefault(item =>
                item.NetId == 0 && !CachedItemSet.Contains(item) &&
                CanCacheClientSceneItem(item) &&
                string.Equals(item.Item?.InventorySpecs?.itemPrefabName, prefabName, StringComparison.Ordinal));
            if (lateSceneItem != null)
            {
                TraceItem("item.late-scene-item-reconciled", lateSceneItem, new() { ["prefabName"] = prefabName });
                SendToCache(lateSceneItem);
            }
        }

        if (CachedItems.TryGetValue(prefabName, out var items) && items.Count > 0)
        {

            var cachedItem = items[items.Count - 1];
            items.RemoveAt(items.Count - 1);
            CachedItemSet.Remove(cachedItem);
            return cachedItem;
        }

        return null;
    }

    private void SendToCache(NetworkedItem netItem)
    {
        if (netItem == null || CachedItemSet.Contains(netItem))
            return;

        string prefabName = netItem?.Item?.InventorySpecs?.itemPrefabName;
        if (string.IsNullOrEmpty(prefabName))
        {
            Multiplayer.LogWarning($"NetworkedItemManager.SendToCache() Item {netItem?.name} has no prefab name; leaving it gated and inactive.");
            netItem?.SetClientNetworkBinding(false);
            netItem?.gameObject.SetActive(false);
            if (netItem != null)
                netItem.NetId = 0;
            return;
        }
        TraceItem("item.cache-enter", netItem, new() { ["prefabName"] = prefabName ?? string.Empty });

        //NetworkLifecycle.Instance.Client.LogDebug(() => $"Caching Spawned Item: {prefabName ?? ""}");

        netItem.SetClientNetworkBinding(false);
        netItem.gameObject.SetActive(false);
        RespawnOnDrop respawn = netItem.Item.GetComponent<RespawnOnDrop>();

        Destroy(respawn);

        //NetworkLifecycle.Instance.Client.LogDebug(() => $"Caching Spawned Item: {prefabName ?? ""}: checkWhileDisabled {respawn.checkWhileDisabled}, ignoreDistanceFromSpawnPosition {respawn.ignoreDistanceFromSpawnPosition}, respawnOnDropThroughFloor {respawn.respawnOnDropThroughFloor}");

        //respawn.checkWhileDisabled = false;
        //respawn.ignoreDistanceFromSpawnPosition = true;
        //respawn.respawnOnDropThroughFloor = false;

        if (SingletonBehaviour<StorageController>.Instance.StorageWorld.ContainsItem(netItem.Item))
        {
            SingletonBehaviour<StorageController>.Instance.RemoveItemFromWorldStorage(netItem.Item);
        }

        if (SingletonBehaviour<StorageController>.Instance.StorageInventory.ContainsItem(netItem.Item))
        {
            SingletonBehaviour<StorageController>.Instance.RemoveItemFromStorageItemList(netItem.Item);
        }

        if (SingletonBehaviour<StorageController>.Instance.StorageLostAndFound.ContainsItem(netItem.Item))
        {
            SingletonBehaviour<StorageController>.Instance.RemoveItemFromStorageItemList(netItem.Item);
        }

        netItem.Item.InventorySpecs.BelongsToPlayer = false;
        netItem.NetId = 0;

        if (!CachedItems.ContainsKey(prefabName))
        {
            CachedItems[prefabName] = new List<NetworkedItem>();
        }
        CachedItems[prefabName].Add(netItem);
        CachedItemSet.Add(netItem);
    }

    private static void TraceSnapshot(string eventName, ItemUpdateData snapshot)
    {
        if (!DebugRuntime.EnabledFor("item") || snapshot == null) return;
        DebugRuntime.Publish("item", eventName, NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: snapshot.ItemNetId.ToString(), data: DebugValueSnapshotter.SnapshotObject(snapshot));
    }

    private static void TraceItem(string eventName, NetworkedItem item, Dictionary<string, object> data)
    {
        if (!DebugRuntime.EnabledFor("item") || item == null) return;
        DebugRuntime.Publish("item", eventName, NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: item.NetId.ToString(), data: data);
    }

    #endregion

    public bool DoNotCreateItem(NetworkedItem item)
    {
        return item != null && DoNotCreateItem(item.TrackedItemType);
    }

    public bool DoNotCreateItem(Type itemType)
    {
        if (
            itemType == typeof(JobOverview) ||
            itemType == typeof(JobBooklet) ||
            itemType == typeof(JobReport) ||
            itemType == typeof(JobExpiredReport) ||
            itemType == typeof(JobMissingLicenseReport)
           )
        {
            return true;
        }

        return false;
    }

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(NetworkedItemManager)}]";
    }
}
