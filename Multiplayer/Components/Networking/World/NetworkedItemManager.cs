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
using Multiplayer.Core.Items;
using Multiplayer.Core.Replication;
using DV.Booklets;
using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Networking.Data.Jobs;
using Multiplayer.Components.Networking.World.Containers;
using Multiplayer.Integrations.Inventory;
using Multiplayer.Integrations.Storage;
using Multiplayer.Components.Networking.World.WorldItems;
using Multiplayer.Networking.Packets.Clientbound;

namespace Multiplayer.Components.Networking.World;

/// <summary>Coordinates item replication queues and delegates subsystem-specific work.</summary>
public partial class NetworkedItemManager : SingletonBehaviour<NetworkedItemManager>
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
    private readonly Dictionary<ItemUpdateData, HashSet<byte>> DestroyRecipients = new();

    /*
     * Client
     */

    // Exact scene-authored Unity objects remain dormant while outside host interest. They are
    // rebound only by stable authored key; dynamic projections are never pooled or reused.
    private readonly HashSet<NetworkedItem> DormantAuthoredClientProjections = new();
    private readonly Queue<NetworkedItem> PendingUnboundClientItems = new();
    private readonly HashSet<NetworkedItem> PendingUnboundClientItemSet = new();
    private readonly Dictionary<string, InventoryItemSpec> ItemPrefabs = new(1024);   //Item prefabs
    private readonly Queue<NetworkedItem> PendingLocalStateObservations = new();
    private readonly HashSet<NetworkedItem> PendingLocalStateObservationSet = new();
    private readonly Dictionary<NetworkedItem, string> PendingLocalStateReasons = new();
    // TEMPORARY: delete these coordinators with TemporaryClientAdoptionCompatibility after all DV
    // runtime item producers have explicit host-authoritative operations.
    private readonly AdoptionCoordinator<NetworkedItem> TemporaryClientAdoptions = new();
    private readonly HostAdoptionRegistry TemporaryHostAdoptions = new();
    private readonly Dictionary<NetworkedItem, float> PendingTrackedValueFinalizations = new();
    private readonly Dictionary<ushort, ItemUpdateData> PendingSpecialItemSnapshots = new();
    private readonly Dictionary<ushort, PendingWorldProjection> PendingWorldProjections = new();
    private readonly HashSet<ushort> ClientLostAndFoundTombstones = new();
    private readonly HashSet<string> ClientDetachedAuthoredSlots = new(StringComparer.Ordinal);
    private bool ClientInitialised = false;
    private readonly AuthoredWorldItemCatalogue AuthoredCatalogue = new();
    private bool authoredCatalogueBuilt;
    private readonly Dictionary<WorldItemCellCoord, HashSet<NetworkedItem>> HostItemsByCell = new();
    private readonly Dictionary<NetworkedItem, WorldItemCellCoord> HostCellByItem = new();
    private readonly HashSet<NetworkedItem> HostNonSpatialItems = new();
    private readonly Dictionary<byte, HashSet<WorldItemCellCoord>> HostPlayerCells = new();
    private bool authoredCatalogueAccepted;
    private NetworkedItemSpatialManager Spatial;
    private AuthoredWorldItemReplenishmentManager Replenishment;

    private sealed class PendingWorldProjection
    {
        public ItemUpdateData Create;
        public readonly List<ItemUpdateData> Updates = new(8);
    }


    /* 
     * Common
     */

    protected override void Awake()
    {
        base.Awake();
        Spatial = new NetworkedItemSpatialManager(this);
        Replenishment = new AuthoredWorldItemReplenishmentManager(this);
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
        Spatial?.Update();
        Replenishment?.Update();
        if (NetworkLifecycle.Instance.IsHost())
            return;

        if (!ClientInitialised)
            return;

        ProcessPendingWorldProjections();

        int remainingBudget = 16;
        while (remainingBudget-- > 0 && PendingUnboundClientItems.Count > 0)
        {
            NetworkedItem item = PendingUnboundClientItems.Dequeue();
            PendingUnboundClientItemSet.Remove(item);
            if (item == null || item.NetId != 0)
                continue;

            if (item.IsSceneAuthored)
            {
                if (!DormantAuthoredClientProjections.Contains(item))
                {
                    TraceItem("item.unbound-authored-item-dormant", item,
                        new() { ["reason"] = "late-authored-activation" });
                    RetireClientProjection(item, "late-authored-activation");
                }
                else
                {
                    item.GateAsSceneObjectAwaitingHostCreate();
                    item.gameObject.SetActive(false);
                }
            }
            else if (IsTemporaryClientAdoptionAllowed(item, out _))
            {
                item.AllowUnboundLocalInteraction();
            }
            else
            {
                TraceItem("item.unbound-client-object-destroyed", item,
                    new() { ["reason"] = "not-host-authorized" });
                RetireClientProjection(item, "not-host-authorized");
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

    internal bool IsClientLostAndFoundTombstoned(ushort itemNetId) =>
        itemNetId != 0 && ClientLostAndFoundTombstones.Contains(itemNetId);

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
        NetworkedLostAndFoundManager.Clear();
        NetworkedColdContainerManager.Clear();
        Spatial?.Clear();
        Replenishment?.Clear();
        ClientDetachedAuthoredSlots.Clear();
        WorldItemPersistenceManager.Clear();
        AuthoritativeItemRegistry.Clear();
    }

    public void AddDirtyItemSnapshot(NetworkedItem netItem, ItemUpdateData snapshot)
    {
        DestroyedItems.Add(snapshot);
        HashSet<byte> recipients = new();
        DestroyRecipients[snapshot] = recipients;
        UnregisterHostWorldItem(netItem);

        foreach (var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.KnownItems.ContainsKey(netItem))
            {
                recipients.Add(player.PlayerId);
                player.KnownItems.Remove(netItem);
            }

            player.AcknowledgedWorldItems.Remove(netItem.NetId);

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


    private void Common_OnTick(uint tick)
    {
        //ProcessReceived();

        if (NetworkLifecycle.Instance.IsHost())
        {
            EnsureAuthoredCatalogue();
            NetworkedLostAndFoundManager.HostTick(tick);
            NetworkedColdContainerManager.HostTick(tick);
            WorldItemPersistenceManager.HostTick();
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
