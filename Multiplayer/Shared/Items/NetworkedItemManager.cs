using System.Collections.Generic;
using System.Linq;
using DV.Utils;
using UnityEngine;
using JetBrains.Annotations;

using Multiplayer.Shared.Session;
using System;
using DV;
using DV.Booklets;

namespace Multiplayer.Shared.Items;

/// <summary>Coordinates item replication queues and delegates subsystem-specific work.</summary>
public partial class NetworkedItemManager : SingletonBehaviour<NetworkedItemManager>
{
    public const float TrackedValueFinalizationGraceSeconds = 0.75f;
    /*
     * Server: the server's part is ServerItemManager (Server/Items), created on the server only.
     * Client: the remote client's part is ClientItemManager (Client/Items), created on a remote
     * client only.
     */

    //Culling distance for items
    public const float MAX_DISTANCE_TO_ITEM = 100f;
    public const float MAX_DISTANCE_TO_ITEM_SQR = MAX_DISTANCE_TO_ITEM * MAX_DISTANCE_TO_ITEM;
    public const float NEARBY_REMOVAL_DELAY = 3f; // 3 seconds delay

    private IItemManagerServerPart serverPart;

    /// <summary>The server's part of the manager; null on a client.</summary>
    public IItemManagerServerPart ServerPart => serverPart ??= ItemServer.Current?.CreateItemManagerPart(this);

    private IItemManagerClientPart clientPart;

    /// <summary>
    ///     The remote client's part of the manager; null on the host, whose own client keeps none of
    ///     it. Made the first time it's asked for on a game that isn't the host.
    /// </summary>
    public IItemManagerClientPart ClientPart =>
        clientPart ??= NetworkLifecycle.Instance.IsHost() ? null : ItemClient.Current?.CreateItemManagerPart(this);

    /*
     * Common
     */

    private readonly Dictionary<string, InventoryItemSpec> ItemPrefabs = new(1024);   //Item prefabs
    private readonly Queue<NetworkedItem> PendingLocalStateObservations = new();
    private readonly HashSet<NetworkedItem> PendingLocalStateObservationSet = new();
    private readonly Dictionary<NetworkedItem, string> PendingLocalStateReasons = new();
    private readonly Dictionary<NetworkedItem, float> PendingTrackedValueFinalizations = new();
    private readonly AuthoredWorldItemCatalogue AuthoredCatalogue = new();
    private bool authoredCatalogueBuilt;
    internal NetworkedItemSpatialManager Spatial { get; private set; }

    protected override void Awake()
    {
        base.Awake();
        Spatial = new NetworkedItemSpatialManager();

        serverPart = ItemServer.Current?.CreateItemManagerPart(this);
    }

    protected void Start()
    {
        // The host's tick runs the server's part; a remote client's runs the client part, whose
        // fall-rescue scan reports its items under the map (#37: it was subscribed on the host only).
        NetworkLifecycle.Instance.OnTick += Common_OnTick;

        BuildPrefabLookup();
    }

    protected void Update()
    {
        ProcessTrackedValueFinalizations();
        ProcessPendingLocalStateObservations();
        // The server's part first: its train-wake manager, the host's leases, replenishment.
        ServerPart?.Update();
        Spatial?.Update();
        // A remote client's: deferred world projections and the unbound items it classifies.
        ClientPart?.Update();
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

    /// <summary>The session's items are gone (the manager is destroyed): presentation holding onto them lets go.</summary>
    internal static event Action Cleared;

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (UnloadWatcher.isQuitting)
            return;

        // Whatever this game was: the server may be gone already (NetworkLifecycle.Stop).
        NetworkLifecycle.Instance.OnTick -= Common_OnTick;
        // Only a part made this session: the server may be gone already.
        serverPart?.Clear();
        Cleared?.Invoke();

        Spatial?.Clear();
        clientPart?.Clear();
    }

    private void Common_OnTick(uint tick)
    {
        if (NetworkLifecycle.Instance.IsHost())
        {
            EnsureAuthoredCatalogue();
            ServerPart?.Tick(tick);
        }
        else
        {
            ClientPart?.Tick(tick);
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

    internal static bool IsJobDocumentPrefab(string prefabName) =>
        string.Equals(prefabName, nameof(JobOverview), StringComparison.Ordinal) ||
        string.Equals(prefabName, nameof(JobBooklet), StringComparison.Ordinal) ||
        string.Equals(prefabName, nameof(JobReport), StringComparison.Ordinal);

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(NetworkedItemManager)}]";
    }
}
