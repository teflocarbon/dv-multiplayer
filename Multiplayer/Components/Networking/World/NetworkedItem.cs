using DV;
using DV.CabControls;
using DV.Customization.Gadgets;
using DV.Interaction;
using DV.InventorySystem;
using DV.Items;
using Multiplayer.Components.Networking.Player;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Collections;
using Multiplayer.Core.Items;

namespace Multiplayer.Components.Networking.World;

public enum ItemState : byte
{
    Dropped,        //belongs to the world
    Thrown,         //was thrown by player
    InHand,         //held by player
    InInventory,    //in player's inventory
    Attached,       //attached to another object (e.g. EOT Lanterns)
    Removed,        //Unattached from mounting point
}

public enum ClientItemUnboundState : byte
{
    None,
    SceneObjectAwaitingHostCreate
}

public class NetworkedItem : IdMonoBehaviour<ushort, NetworkedItem>
{
    #region Lookup Cache
    private static readonly Dictionary<ItemBase, NetworkedItem> itemBaseToNetworkedItem = new(4096);

    public static Dictionary<ItemBase, NetworkedItem>.ValueCollection GetAll() => itemBaseToNetworkedItem.Values;

    public static bool Get(ushort netId, out NetworkedItem obj)
    {
        bool b = Get(netId, out IdMonoBehaviour<ushort, NetworkedItem> rawObj);
        obj = (NetworkedItem)rawObj;
        return b;
    }

    public static bool TryGet(ushort netId, out NetworkedItem obj)
    {
        bool b = TryGet(netId, out IdMonoBehaviour<ushort, NetworkedItem> rawObj);
        obj = (NetworkedItem)rawObj;
        return b;
    }

    public static bool GetItem(ushort netId, out ItemBase obj)
    {
        bool b = Get(netId, out NetworkedItem networkedItem);
        obj = b ? networkedItem.Item : null;
        return b;
    }

    public static bool TryGetNetworkedItem(ItemBase item, out NetworkedItem networkedItem)
    {
        return itemBaseToNetworkedItem.TryGetValue(item, out networkedItem);
    }

    public static bool TryGetNetId(ItemBase item, out ushort netID)
    {
        if (itemBaseToNetworkedItem.TryGetValue(item, out var networkedItem))
        {
            netID = networkedItem.NetId;
            return true;
        }

        netID = 0;
        return false;
    }
    #endregion

    #region Server Variables
    public ServerPlayer BelongsTo { get; private set; }
    #endregion

    #region Common Variables
    public ItemBase Item { get; private set; }
    private GrabHandlerItem grabHandler;
    private SnappableItem snappableItem;
    private Component trackedItem;
    private readonly List<object> trackedValues = [];
    public bool UsefulItem { get; private set; }
    public Type TrackedItemType { get; private set; }
    public uint LastDirtyTick { get; private set; }
    private bool initialised;
    private bool registrationComplete = false;
    private const int MaxPendingSnapshots = 256;
    private readonly PendingSnapshotQueue<ItemUpdateData> pendingSnapshots = new(
        MaxPendingSnapshots,
        snapshot => snapshot?.AuthorityRevision ?? 0,
        snapshot => snapshot?.UpdateType == ItemUpdateData.ItemUpdateType.FullSync);

    //Track dirty states
    private bool createdDirty = true;   //if set, we created this item dirty and have not sent an update
    private ItemState lastState;
    private ItemWireStateProjection lastSentProjection;
    private bool hasLastSentState;
    private bool stateDirty;
    private bool wasThrown;
    private bool wasRemoved;

    private Vector3 thrownPosition;
    private Quaternion thrownRotation;
    private Vector3 throwDirection;

    private bool processingAsHost = false;
    private bool applyingRemoteSnapshot;
    private bool hostStateObservationPending;
    private bool clientBindingGateApplied;
    private bool interactionAllowedBeforeBindingGate;
    internal ClientItemUnboundState UnboundState { get; private set; }
    #endregion

    #region Client Variables
    public byte playerBelongsToId; // 0 means no owner
    internal ItemState DebugCurrentState => GetItemState();
    internal ItemState DebugLastState => lastState;
    public NetworkedPlayer playerBelongsTo;
    public uint AuthorityRevision { get; private set; }
    public byte PersistentOwnerPlayerId { get; private set; }
    public byte InventoryClaimPlayerId { get; private set; }
    public int InventoryClaimSlot { get; private set; } = -1;
    public ItemInventoryClaimFlags InventoryClaimFlags { get; private set; }
    public ItemTransitionReason LastTransitionReason { get; private set; }
    public bool IsForeignOwned => PersistentOwnerPlayerId != 0 &&
        PersistentOwnerPlayerId != (NetworkLifecycle.Instance?.Client?.PlayerId ?? 0);
    #endregion

    protected override bool IsIdServerAuthoritative => true;

    #region Unity Callbacks
    protected override void Awake()
    {
        base.Awake();

        if (NetworkLifecycle.Instance.IsHost())
            NetworkedItemManager.Instance.CheckInstance(); //Ensure the NetworkedItemManager is initialised

        Register();
    }

    protected void Start()
    {
        if (!initialised)
            Register();

        NetworkedItemManager.Instance?.ScheduleTrackedValueFinalization(this);

        if (!NetworkLifecycle.Instance.IsHost() && NetId == 0)
            NetworkedItemManager.Instance?.RegisterUnboundClientItem(this);

        EntityDebugRegistry.RegisterItem(this);
        DebugRuntime.Publish("item", "item.registered", NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: NetId.ToString(), data: EntityDebugRegistry.ItemState(this));
    }

    protected void OnEnable()
    {
        NetworkedItemManager.Instance?.ScheduleTrackedValueFinalization(this);
        if (!NetworkLifecycle.Instance.IsHost() && NetId == 0)
            NetworkedItemManager.Instance?.RegisterUnboundClientItem(this);
    }

    protected void LateUpdate()
    {
        if (NetworkLifecycle.Instance.IsHost() && hostStateObservationPending)
            return;
        ItemState currentState = GetItemState();
        if (!NetworkLifecycle.Instance.IsHost() && NetId == 0 && !stateDirty)
            return;
        bool remotePlayerAuthoritative = IsRemotePlayerAuthoritative();
        if (!stateDirty && lastState == currentState &&
            (remotePlayerAuthoritative || !HasNetworkStateProjectionChanged(currentState)) &&
            !HasDirtyValues())
            return;
        ProcessLocalStateObservation("late-update");
    }

    protected override void OnDestroy()
    {
        bool suppressNetworkDestroy = UnloadWatcher.isQuitting || UnloadWatcher.isUnloading;

        if (!suppressNetworkDestroy)
            DebugRuntime.Publish("item", "item.destroyed", NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
                entityType: "Item", entityId: NetId.ToString(), data: EntityDebugRegistry.ItemState(this));
        EntityDebugRegistry.Unregister("Item", NetId.ToString());
        DebugDesyncDetector.Forget(NetId);

        if (!suppressNetworkDestroy && NetworkLifecycle.Instance.IsHost())
        {
            var updateData = CreateUpdateData(ItemUpdateData.ItemUpdateType.Destroy);
            if (updateData != null)
                NetworkedItemManager.Instance.AddDirtyItemSnapshot(this, updateData);
        }

        if (Item != null)
        {
            Item.Grabbed -= OnGrabbed;
            Item.Ungrabbed -= OnUngrabbed;
            itemBaseToNetworkedItem.Remove(Item);
        }
        else
        {
            Multiplayer.LogWarning($"NetworkedItem.OnDestroy({name}, {NetId}) Item is null!");
        }

        AuthoritativeItemRegistry.Remove(NetId);
        base.OnDestroy();
    }
    #endregion

    #region Server
    internal void ServerInitialiseAdoptedItem(ServerPlayer owner, ItemUpdateData snapshot)
    {
        if (owner == null || NetId == 0 || snapshot == null)
            return;

        snapshot.ItemNetId = NetId;
        snapshot.UpdateType = ItemUpdateData.ItemUpdateType.FullSync;
        if (!AuthoritativeItemRegistry.TryApplyTransition(this, snapshot, owner,
                ItemTransitionReason.ClientAdoption, true, out string rejectionReason))
        {
            Multiplayer.LogWarning($"Unable to initialize adopted item {NetId}: {rejectionReason}");
            return;
        }
        UpdateHostPossessor(snapshot.PlayerId);
        ApplyAuthorityMetadata(snapshot);
        ReceiveSnapshot(snapshot);
    }

    public void Server_ReceiveItemUpdate(ItemUpdateData snapshot, ServerPlayer senderPlayer)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(snapshot, DebugRuntimeSide.Server, "Item", snapshot?.ItemNetId.ToString());
        //TODO: rollback if validation fails
        if (!ValidateUpdate(snapshot, senderPlayer))
            return;

        if (!AuthoritativeItemRegistry.TryApplyTransition(this, snapshot, senderPlayer,
                ItemTransitionReason.ClientState, false, out string authorityRejection))
        {
            DebugTrace.Validation("item", "Item", NetId.ToString(), false, authorityRejection, DebugRuntimeSide.Server);
            return;
        }
        DebugTrace.Validation("item", "Item", NetId.ToString(), true, "accepted", DebugRuntimeSide.Server);

        Multiplayer.LogDebug(() => $"NetworkedItem.Server_ReceiveItemUpdate() NetId: {snapshot?.ItemNetId}, ItemState: {snapshot?.ItemState}, Player: {senderPlayer.DisplayName}");
        // FullSync is a composite flag (ItemState | ItemPosition | ObjectState).
        // Including it in a HasAnyFlag mask also matches a plain ObjectState packet,
        // incorrectly turning tracked-value-only updates into hand/world transitions.
        bool appliesItemState = ItemUpdateData.IncludesItemState(snapshot.UpdateType);
        if (appliesItemState)
        {
            UpdateHostPossessor(snapshot.PlayerId);
        }

        // Allow host to process packet if not from a local client
        if (!NetworkLifecycle.Instance.IsClientRunning || (NetworkLifecycle.Instance.IsClientRunning && senderPlayer.PlayerId != NetworkLifecycle.Instance.Server.SelfId))
        {
            processingAsHost = true;
            ReceiveSnapshot(snapshot);
        }

        //NetworkedItemManager.Instance.AddDirtyItemSnapshot(this, snapshot);
        // The sender also receives the canonical revision/owner projection. Applying an
        // acknowledgement is idempotent and prevents the next transition using a stale revision.
        NetworkLifecycle.Instance.Server.SendItemUpdatePacket(snapshot);
    }

    private bool ValidateUpdate(ItemUpdateData snapshot, ServerPlayer senderPlayer)
    {
        // Clients can not spawn or destroy items
        if (snapshot.UpdateType.HasAnyFlag(ItemUpdateData.ItemUpdateType.Create | ItemUpdateData.ItemUpdateType.Destroy))
        {
            DebugTrace.Validation("item", "Item", NetId.ToString(), false, "client-create-or-destroy-not-allowed", DebugRuntimeSide.Server);
            return false;
        }

        if (snapshot.States != null && snapshot.States.TryGetValue(NetworkedPageBookState.TrackedValueKey, out object pageValue))
        {
            if (pageValue is not int requestedPage)
            {
                DebugTrace.Validation("item", "Item", NetId.ToString(), false, "page-index-type-invalid", DebugRuntimeSide.Server);
                return false;
            }
            PageBook pageBook = GetComponentInChildren<PageBook>(true);
            if (pageBook == null)
            {
                DebugTrace.Validation("item", "Item", NetId.ToString(), false, "pagebook-not-present", DebugRuntimeSide.Server);
                return false;
            }
            PageBookApplyPlan pagePlan = PageBookSyncPlanner.Plan(requestedPage, pageBook.PagesGenerated, pageBook.PageNum);
            if (pagePlan.Status == PageBookApplyStatus.Reject)
            {
                DebugTrace.Validation("item", "Item", NetId.ToString(), false, pagePlan.Reason, DebugRuntimeSide.Server);
                return false;
            }
        }

        return true;
    }

    internal void ApplyHostLocalCanonicalTransition(ItemUpdateData snapshot, ServerPlayer actor)
    {
        if (snapshot == null || actor == null)
            return;
        string parentBefore = ParentPath();
        PublishUnityState("item.ownership-transition.before", new()
        {
            ["actorPlayerId"] = actor.PlayerId,
            ["targetState"] = snapshot.ItemState.ToString(),
            ["transitionReason"] = snapshot.TransitionReason.ToString()
        });
        PrepareForStateChange(snapshot.PlayerId, snapshot.ItemState);
        UpdateHostPossessor(snapshot.PlayerId);
        ApplyAuthorityMetadata(snapshot);
        PublishParentChange(parentBefore, "host-local-authority-transfer");
        PublishUnityState("item.ownership-transition.after", new()
        {
            ["actorPlayerId"] = actor.PlayerId,
            ["targetState"] = snapshot.ItemState.ToString(),
            ["transitionReason"] = snapshot.TransitionReason.ToString()
        });
    }

    private void UpdateHostPossessor(byte playerId)
    {
        BelongsTo = null;
        if (playerId != 0 && NetworkLifecycle.Instance.Server != null &&
            NetworkLifecycle.Instance.Server.TryGetServerPlayer(playerId, out ServerPlayer player))
            BelongsTo = player;
    }

    internal void ApplyServerCanonicalSnapshot(ItemUpdateData snapshot)
    {
        if (snapshot == null)
            return;
        UpdateHostPossessor(snapshot.PlayerId);
        ApplyAuthorityMetadata(snapshot);
        processingAsHost = true;
        ReceiveSnapshot(snapshot);
        processingAsHost = false;
    }
    #endregion

    #region Client
    private void OnUngrabbed(ControlImplBase obj)
    {
        MarkLocalStateDirty("released");
    }

    private void OnGrabbed(ControlImplBase obj)
    {
        playerBelongsToId = NetworkLifecycle.Instance.Client.PlayerId;
        MarkLocalStateDirty("grabbed");
    }

    public void OnThrow(Vector3 direction)
    {
        playerBelongsToId = 0;

        //block a received throw from 
        if (wasThrown)
        {
            wasThrown = false;
            return;
        }

        throwDirection = direction;
        thrownPosition = Item.transform.position - WorldMover.currentMove;
        thrownRotation = Item.transform.rotation;

        //Multiplayer.LogDebug(() => $"NetworkedItem.OnThrow() netId: {NetId}, Name: {name}, Raw Position: {Item.transform.position}, Position: {thrownPosition}, Rotation: {thrownRotation}, Direction: {throwDirection}");

        wasThrown = true;
        MarkLocalStateDirty("thrown");
    }

    public void OnRemove()
    {
        playerBelongsToId = 0;
        playerBelongsTo = null;
        wasRemoved = true;
        MarkLocalStateDirty("removed");
    }
    #endregion

    #region Common
    #region Initialisation

    public T GetTrackedItem<T>() where T : Component
    {
        return UsefulItem ? trackedItem as T : null;
    }

    public void Initialize<T>(T item, ushort netId = 0, bool createDirty = true) where T : Component
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.Initialize<{typeof(T)}>(netId: {netId}, name: {name}, createDirty: {createdDirty})");

        if (netId != 0)
        {
            if (!NetworkLifecycle.Instance.IsHost())
                NetworkedItemManager.Instance?.ResolveAuthoritativeBindingCollision(this, netId, typeof(T));
            NetId = netId;
            SetClientNetworkBinding(true);
        }

        trackedItem = item;
        TrackedItemType = typeof(T);
        UsefulItem = true;

        createdDirty = createDirty;

        if (Item == null)
            Register();

        NetworkedItemManager.Instance?.ScheduleTrackedValueFinalization(this);
        NetworkedItemManager.Instance?.ApplyPendingSpecialSnapshot(this);

    }

    private bool Register()
    {
        if (initialised)
            return false;

        try
        {
            if (!TryGetComponent(out ItemBase itemBase))
            {
                Multiplayer.LogError($"NetworkedItem.Register() Unable to find ItemBase for {name}");
                return false;
            }

            Item = itemBase;
            itemBaseToNetworkedItem[Item] = this;

            Item.Grabbed += OnGrabbed;
            Item.Ungrabbed += OnUngrabbed;

            //Find special interaction components
            TryGetComponent<GrabHandlerItem>(out grabHandler);
            TryGetComponent<SnappableItem>(out snappableItem);
            NetworkedPageBookState.TryRegister(this);

            if (!NetworkLifecycle.Instance.IsHost() && NetId == 0)
                NetworkedItemManager.Instance?.RegisterUnboundClientItem(this);

            lastState = GetItemState();
            stateDirty = false;

            initialised = true;
            NetworkedItemManager.Instance?.ScheduleTrackedValueFinalization(this);
            return true;
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"NetworkedItem.Register() Unable to find ItemBase for {name}\r\n{ex.Message}");
            return false;
        }
    }

    internal void SetClientNetworkBinding(bool isBound)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        if (isBound && NetId != 0)
        {
            UnboundState = ClientItemUnboundState.None;

            // A non-host item with an authoritative ID represents an object the server
            // has already created.  Clear the local creation edge immediately rather
            // than waiting for its first snapshot to apply: tracked-value setup can
            // defer that snapshot, and LateUpdate must not emit a client Create while
            // the item is waiting in that queue.
            createdDirty = false;
        }

        if (grabHandler == null)
            TryGetComponent(out grabHandler);
        if (grabHandler == null)
            return;

        if (!isBound)
        {
            if (!clientBindingGateApplied)
            {
                interactionAllowedBeforeBindingGate = grabHandler.interactionAllowed;
                clientBindingGateApplied = true;
            }
            grabHandler.interactionAllowed = false;
            return;
        }

        if (clientBindingGateApplied)
        {
            grabHandler.interactionAllowed = interactionAllowedBeforeBindingGate;
            clientBindingGateApplied = false;
        }
    }

    internal void AllowUnboundLocalInteraction()
    {
        UnboundState = ClientItemUnboundState.None;
        SetClientNetworkBinding(true);
    }

    internal void GateAsSceneObjectAwaitingHostCreate()
    {
        UnboundState = ClientItemUnboundState.SceneObjectAwaitingHostCreate;
        SetClientNetworkBinding(false);
    }

    internal void CompleteItemAdoption(ItemAdoptionResultData result)
    {
        if (result.AssignedNetId == 0)
            return;
        NetId = result.AssignedNetId;
        ApplyAuthorityMetadata(new ItemUpdateData
        {
            AuthorityRevision = result.AuthorityRevision,
            PersistentOwnerPlayerId = result.PersistentOwnerPlayerId,
            InventoryClaimPlayerId = result.InventoryClaimPlayerId,
            InventoryClaimSlot = result.InventoryClaimSlot,
            InventoryClaimFlags = result.InventoryClaimFlags,
            TransitionReason = ItemTransitionReason.ClientAdoption
        });
        UnboundState = ClientItemUnboundState.None;
        SetClientNetworkBinding(true);
        createdDirty = false;
        hasLastSentState = false;
        stateDirty = true;
        ProcessLocalStateObservation("adoption-complete");
    }

    internal ItemAdoptionRequestData CreateItemAdoptionRequest(string token)
    {
        ItemState currentState = GetItemState();
        lastState = currentState;
        playerBelongsToId = currentState is ItemState.InHand or ItemState.InInventory
            ? NetworkLifecycle.Instance.Client.PlayerId
            : (byte)0;
        if (PersistentOwnerPlayerId == 0 && (Item?.IsEssential() == true ||
                Item?.InventorySpecs?.BelongsToPlayer == true))
            PersistentOwnerPlayerId = NetworkLifecycle.Instance.Client.PlayerId;
        ItemUpdateData snapshot = CreateUpdateData(ItemUpdateData.ItemUpdateType.FullSync);
        return new ItemAdoptionRequestData
        {
            AdoptionToken = token,
            PrefabName = Item?.InventorySpecs?.ItemPrefabName ?? name,
            Position = transform.position - WorldMover.currentMove,
            Rotation = transform.rotation,
            Snapshot = snapshot
        };
    }

    private void MarkLocalStateDirty(string reason)
    {
        // ForceEndInteraction, inventory purges and GrabHandlerItem.Throw raise the same base-game
        // callbacks as a local interaction. While projecting an authoritative snapshot those
        // callbacks are side effects, not new player intent, and must not enqueue an echo packet.
        if (applyingRemoteSnapshot)
            return;
        stateDirty = true;
        if (!NetworkLifecycle.Instance.IsHost())
            NetworkedItemManager.Instance?.QueueLocalStateObservation(this, reason);
    }

    internal void ProcessLocalStateObservation(string reason)
    {
        if (Item == null && !Register())
            return;
        ItemState previousObservedState = lastState;
        ItemState currentState = GetItemState();
        bool networkStateChanged = HasNetworkStateProjectionChanged(currentState);
        if (currentState != previousObservedState || networkStateChanged)
            stateDirty = true;
        PublishLocalState("item.local-state-observed", currentState, previousObservedState, reason, string.Empty);
        lastState = currentState;

        PublishLocalState(networkStateChanged ? "item.local-state-changed" : "item.local-state-unchanged",
            currentState, previousObservedState, reason, string.Empty);

        // Host-local interactions are authoritative already. Leave the dirty state for
        // NetworkedItemManager.ProcessChanged() instead of sending a client packet back
        // through the host's validation handler.
        if (NetworkLifecycle.Instance.IsHost())
        {
            hostStateObservationPending = true;
            return;
        }

        byte localPlayerId = NetworkLifecycle.Instance.Client?.PlayerId ?? 0;
        if (IsRemotePlayerAuthoritative())
        {
            stateDirty = false;
            MarkValuesClean();
            PublishLocalState("item.snapshot-suppressed", currentState, previousObservedState, reason,
                "remote-player-authoritative");
            return;
        }

        if (NetId == 0)
        {
            NetworkedItemManager.Instance?.RequestItemAdoption(this, reason);
            stateDirty = false;
            PublishLocalState("item.snapshot-suppressed", currentState, previousObservedState, reason,
                "awaiting-host-item-adoption");
            return;
        }

        ItemUpdateData snapshot = GetSnapshot();
        if (snapshot == null)
        {
            PublishLocalState("item.snapshot-suppressed", currentState, previousObservedState, reason,
                "no-state-or-tracked-value-change");
            return;
        }

        if (!processingAsHost)
            NetworkLifecycle.Instance.Client?.SendItemUpdatePacket(snapshot);
        else
            processingAsHost = false;
    }

    private void PublishLocalState(string eventName, ItemState currentState, ItemState previousObservedState,
        string reason, string suppressionReason)
    {
        if (!DebugRuntime.EnabledFor("item"))
            return;
        Dictionary<string, object> data = LocalStateDebugData();
        data["currentState"] = currentState.ToString();
        data["previousObservedState"] = previousObservedState.ToString();
        data["previousSentState"] = hasLastSentState ? lastSentProjection.State.ToString() : "none";
        data["previousSentPlacementPlayerId"] = hasLastSentState ? lastSentProjection.PlacementPlayerId : 0;
        data["previousSentAttachedCarNetId"] = hasLastSentState ? lastSentProjection.AttachedCarNetId : 0;
        data["previousSentAttachedFront"] = hasLastSentState && lastSentProjection.AttachedFront;
        data["trigger"] = reason ?? string.Empty;
        data["suppressionReason"] = suppressionReason ?? string.Empty;
        DebugRuntime.Publish("item", eventName,
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: NetId.ToString(), data: data);
    }

    internal Dictionary<string, object> LocalStateDebugData()
    {
        int inventorySlot = -1;
        string inventoryContainer = string.Empty;
        try
        {
            var container = Inventory.Instance?.ItemContainerRegistry?.GetItemContainerAndIndex(gameObject);
            if (container.HasValue)
            {
                inventoryContainer = container.Value.Item1?.GetType().Name ?? string.Empty;
                inventorySlot = container.Value.Item2;
            }
        }
        catch { }
        return new Dictionary<string, object>
        {
            ["itemNetId"] = NetId,
            ["prefab"] = Item?.InventorySpecs?.ItemPrefabName ?? name,
            ["unityInstanceId"] = gameObject.GetInstanceID(),
            ["inventorySlot"] = inventorySlot,
            ["inventoryContainer"] = inventoryContainer,
            ["equippedSlot"] = Item?.IsGrabbed() == true ? "active-hand" : string.Empty,
            ["activeSelf"] = gameObject.activeSelf,
            ["activeInHierarchy"] = gameObject.activeInHierarchy,
            ["authorityRevision"] = AuthorityRevision,
            ["persistentOwnerPlayerId"] = PersistentOwnerPlayerId,
            ["placementPlayerId"] = playerBelongsToId,
            ["hostPossessorPlayerId"] = BelongsTo?.PlayerId ?? 0,
            ["inventoryClaimPlayerId"] = InventoryClaimPlayerId,
            ["inventoryClaimSlot"] = InventoryClaimSlot,
            ["inventoryClaimFlags"] = InventoryClaimFlags.ToString(),
            ["transitionReason"] = LastTransitionReason.ToString()
        };
    }
    #endregion

    #region Item Value Tracking
    internal bool TrackedValuesFinalised => registrationComplete;

    public void RegisterTrackedValue<T>(string key, Func<T> valueGetter, Action<T> valueSetter, Func<T, T, bool> thresholdComparer = null, bool serverAuthoritative = false)
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.RegisterTrackedValue(\"{key}\", {valueGetter != null}, {valueSetter != null}, {thresholdComparer != null}, {serverAuthoritative}) itemNetId {NetId}, item name: {name}");
        if (registrationComplete)
            DebugRuntime.Publish("item", "item.tracked-value-registered-late", NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
                DebugSeverity.Warning, "Item", NetId.ToString(), new() { ["key"] = key ?? string.Empty, ["trackedItemType"] = TrackedItemType?.FullName ?? string.Empty });
        trackedValues.Add(new TrackedValue<T>(key, valueGetter, valueSetter, thresholdComparer, serverAuthoritative));
    }

    public void FinaliseTrackedValues()
    {
        FinaliseTrackedValues("explicit");
    }

    internal void FinaliseTrackedValuesAutomatically()
    {
        FinaliseTrackedValues("manager-grace-expired");
    }

    private void FinaliseTrackedValues(string reason)
    {
        if (registrationComplete)
            return;

        registrationComplete = true;
        int queuedCount = pendingSnapshots.Count;

        while (pendingSnapshots.TryDequeue(out ItemUpdateData pending))
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.FinaliseTrackedValues() itemNetId: {NetId}, item name: {name}. Dequeuing");
            DeferredSnapshotDrainPlan drainPlan = DeferredSnapshotDrainPlanner.Plan(
                pending.AuthorityRevision, AuthorityRevision, pending.States is { Count: > 0 });
            switch (drainPlan.Action)
            {
                case DeferredSnapshotDrainAction.ApplyFullSnapshot:
                    ApplySnapshot(pending);
                    break;
                case DeferredSnapshotDrainAction.ApplyTrackedStateOnly:
                    ApplyDeferredTrackedState(pending, drainPlan.Reason);
                    break;
                case DeferredSnapshotDrainAction.SkipStaleSnapshot:
                    PublishDeferredDrain("item.pending-snapshot-stale-skipped", pending,
                        drainPlan.Reason);
                    break;
            }
            DebugDiagnostics.QueueApplied("ItemPendingSnapshot", NetId.ToString(), pendingSnapshots.Count, NetworkLifecycle.Instance.Tick);
        }

        DebugDiagnostics.ResolveDependency("Item", NetId.ToString(), "tracked-values-not-finalised");
        DebugRuntime.Publish("item", "item.tracked-values-finalized", NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: NetId.ToString(), data: new()
            {
                ["reason"] = reason,
                ["trackedItemType"] = TrackedItemType?.FullName ?? string.Empty,
                ["trackedValueCount"] = trackedValues.Count,
                ["drainedSnapshotCount"] = queuedCount
            });
    }

    private bool HasDirtyValues()
    {
        //clients should only send values that are not server authoritative
        if (!NetworkLifecycle.Instance.IsHost())
            return trackedValues.Any(tv => ((dynamic)tv).IsDirty && !((dynamic)tv).ServerAuthoritative);
        else
            return trackedValues.Any(tv => ((dynamic)tv).IsDirty);
    }

    private Dictionary<string, object> GetDirtyStateData()
    {
        return TrackedStateComposer.Compose(GetTrackedStateEntries(),
            TrackedStateCompositionMode.Delta, NetworkLifecycle.Instance.IsHost());
    }

    private Dictionary<string, object> GetAllStateData()
    {
        return TrackedStateComposer.Compose(GetTrackedStateEntries(),
            TrackedStateCompositionMode.FullSync, NetworkLifecycle.Instance.IsHost());
    }

    private IEnumerable<TrackedStateEntry> GetTrackedStateEntries()
    {
        foreach (var trackedValue in trackedValues)
            yield return new TrackedStateEntry(
                ((dynamic)trackedValue).Key,
                ((dynamic)trackedValue).GetValueAsObject(),
                ((dynamic)trackedValue).IsDirty,
                ((dynamic)trackedValue).ServerAuthoritative);
    }

    private void MarkValuesClean()
    {
        foreach (var trackedValue in trackedValues)
        {
            ((dynamic)trackedValue).MarkClean();
        }
    }

    #endregion

    public ItemUpdateData GetSnapshot()
    {
        ItemUpdateData snapshot;
        ItemUpdateData.ItemUpdateType updateType = ItemUpdateData.ItemUpdateType.None;

        bool hasDirtyVals = HasDirtyValues();

        if (Item == null && Register() == false)
        {
            hostStateObservationPending = false;
            return null;
        }

        if (!stateDirty && !hasDirtyVals)
        {
            hostStateObservationPending = false;
            return null;
        }

        ItemState currentState = GetItemState();

        if (!createdDirty)
        {
            // Inventory, grab and equip operations often raise several callbacks for one
            // transition. Dirty means "re-evaluate", not "send regardless". Only put an
            // ItemState on the wire when the wire projection differs from the last one
            // sent (state plus holder for inventory/hand states). Thrown remains covered
            // because wasThrown makes GetItemState() return the one-shot Thrown state.
            if (HasNetworkStateProjectionChanged(currentState))
                updateType |= ItemUpdateData.ItemUpdateType.ItemState;

            if (hasDirtyVals)
            {
                Multiplayer.LogDebug(GetDirtyValuesDebugString);
                updateType |= ItemUpdateData.ItemUpdateType.ObjectState;
            }
        }
        else
        {
            updateType = ItemUpdateData.ItemUpdateType.Create;
        }

        //no changes this snapshot
        if (updateType == ItemUpdateData.ItemUpdateType.None)
        {
            hostStateObservationPending = false;
            stateDirty = false;
            wasThrown = false;
            wasRemoved = false;
            return null;
        }

        lastState = currentState;
        LastDirtyTick = NetworkLifecycle.Instance.Tick;
        snapshot = CreateUpdateData(updateType);
        if (snapshot != null)
        {
            lastSentProjection = ItemWireStateComparer.StableBaselineAfterSend(
                ProjectionFromSnapshot(snapshot));
            hasLastSentState = true;
            // Thrown is a one-shot wire transition, not a stable Unity state. Once the
            // throw has been emitted the base game represents the item as an ordinary
            // ungrabbed world item. Treat that as the new observation baseline so the
            // next LateUpdate does not immediately emit Dropped and cancel momentum.
            lastState = ObservationBaselineAfterSnapshot(currentState);
        }

        createdDirty = false;
        stateDirty = false;
        wasThrown = false;
        wasRemoved = false;

        MarkValuesClean();

        return snapshot;
    }

    public void ReceiveSnapshot(ItemUpdateData snapshot)
    {
        if (snapshot == null || snapshot.UpdateType == ItemUpdateData.ItemUpdateType.None)
            return;

        if (snapshot.AuthorityRevision != 0 && snapshot.AuthorityRevision < AuthorityRevision)
        {
            DebugRuntime.Publish("item", "item.stale-authority-snapshot-rejected",
                NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
                DebugSeverity.Warning, "Item", NetId.ToString(), new()
                {
                    ["currentRevision"] = AuthorityRevision,
                    ["receivedRevision"] = snapshot.AuthorityRevision,
                    ["transitionReason"] = snapshot.TransitionReason.ToString()
                });
            return;
        }
        ApplyAuthorityMetadata(snapshot);

        Dictionary<string, object> receivedDebugData = DebugTrace.ItemSnapshotData(snapshot);
        snapshot.DebugCorrelationFingerprint = receivedDebugData.TryGetValue("stateFingerprint", out object fingerprint)
            ? Convert.ToString(fingerprint)
            : string.Empty;
        DebugRuntime.Publish("item", "item.snapshot-received", NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: snapshot.ItemNetId.ToString(), data: receivedDebugData);
        DebugDesyncDetector.Remember(snapshot);

        if (!registrationComplete && snapshot.States is { Count: > 0 })
        {
            if (ItemUpdateData.IncludesItemState(snapshot.UpdateType))
            {
                // Establish the authoritative wire baseline while tracked values wait for
                // registration. An unchanged job-created object must not echo a redundant
                // Dropped update merely because its Create has not drained yet. A real local
                // interaction still differs from this baseline and is sent normally.
                lastState = ObservationBaselineAfterSnapshot(snapshot.ItemState);
                lastSentProjection = ItemWireStateComparer.StableBaselineAfterSend(
                    ProjectionFromSnapshot(snapshot));
                hasLastSentState = true;
            }
            Multiplayer.Log($"NetworkedItem.ReceiveSnapshot() netId: {snapshot?.ItemNetId}, ItemUpdateType: {snapshot?.UpdateType}. Queuing");
            PendingEnqueueResult enqueue = pendingSnapshots.Enqueue(snapshot);
            if (enqueue.Accepted)
                DebugDiagnostics.QueueReceived("ItemPendingSnapshot", NetId.ToString(), pendingSnapshots.Count,
                    NetworkLifecycle.Instance.Tick, pendingSnapshots.Count > 1 ? NetworkLifecycle.Instance.Tick - 1 : 0);
            else
                DebugRuntime.Publish("item", "item.pending-snapshot-rejected",
                    NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
                    enqueue.Status == PendingEnqueueStatus.CapacityExceeded ? DebugSeverity.Error : DebugSeverity.Warning,
                    "Item", NetId.ToString(), new()
                    {
                        ["reason"] = enqueue.Status.ToString(),
                        ["queueDepth"] = pendingSnapshots.Count,
                        ["queueCapacity"] = MaxPendingSnapshots,
                        ["authorityRevision"] = snapshot.AuthorityRevision,
                        ["updateType"] = snapshot.UpdateType.ToString()
                    });
            DebugDiagnostics.ReportDependency("Item", NetId.ToString(), "tracked-values-not-finalised", $"pending={pendingSnapshots.Count}");
            DebugRuntime.Publish("item", "item.snapshot-deferred", NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
                DebugSeverity.Warning, "Item", NetId.ToString(), new()
                {
                    ["reason"] = "tracked-values-not-finalised",
                    ["queueDepth"] = pendingSnapshots.Count,
                    ["queueCapacity"] = MaxPendingSnapshots,
                    ["enqueueStatus"] = enqueue.Status.ToString(),
                    ["supersededSnapshotCount"] = enqueue.SupersededCount,
                    ["automaticFinalizationGraceSeconds"] = NetworkedItemManager.TrackedValueFinalizationGraceSeconds,
                    ["stateFingerprint"] = snapshot.DebugCorrelationFingerprint
                });
            NetworkedItemManager.Instance?.ScheduleTrackedValueFinalization(this);
            return;
        }

        if (!registrationComplete)
        {
            // Placement does not depend on tracked-value registration. In particular,
            // many generic items have no tracked values at all; holding their Create
            // packet for the registration grace period leaves a live clone at the
            // origin and can expose a visibly falling debug label. Only packets which
            // actually carry tracked state need to wait for item-specific patches.
            DebugRuntime.Publish("item", "item.snapshot-applied-before-finalization",
                NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
                entityType: "Item", entityId: NetId.ToString(), data: new()
                {
                    ["reason"] = "no-tracked-state-in-packet",
                    ["authorityRevision"] = snapshot.AuthorityRevision,
                    ["updateType"] = snapshot.UpdateType.ToString()
                });
            NetworkedItemManager.Instance?.ScheduleTrackedValueFinalization(this);
        }

        ApplySnapshot(snapshot);
    }

    private void ApplyDeferredTrackedState(ItemUpdateData snapshot, string reason)
    {
        PublishDeferredDrain("item.pending-snapshot-stale-placement-skipped", snapshot, reason);
        using IDisposable debugScope = DebugTrace.BeginApply("item", "item.pending-tracked-state-apply",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            "Item", NetId.ToString(), () => DebugApplyState(snapshot));
        if (snapshot.States is { Count: > 0 })
            ApplyTrackedValues(snapshot.States);
        MarkValuesClean();
        EntityDebugRegistry.UpdateState("Item", NetId.ToString(), EntityDebugRegistry.ItemState(this));
    }

    private void PublishDeferredDrain(string eventName, ItemUpdateData snapshot, string reason)
    {
        DebugRuntime.Publish("item", eventName,
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            DebugSeverity.Warning, "Item", NetId.ToString(), new()
            {
                ["reason"] = reason ?? string.Empty,
                ["pendingAuthorityRevision"] = snapshot?.AuthorityRevision ?? 0,
                ["currentAuthorityRevision"] = AuthorityRevision,
                ["pendingUpdateType"] = snapshot?.UpdateType.ToString() ?? string.Empty,
                ["stateFingerprint"] = snapshot?.DebugCorrelationFingerprint ?? string.Empty,
                ["trackedStateCount"] = snapshot?.States?.Count ?? 0
            });
    }

    private void ApplySnapshot(ItemUpdateData snapshot)
    {
        applyingRemoteSnapshot = true;
        try
        {
            ApplySnapshotCore(snapshot);
        }
        finally
        {
            applyingRemoteSnapshot = false;
        }
    }

    private void ApplySnapshotCore(ItemUpdateData snapshot)
    {
        ApplyAuthorityMetadata(snapshot);
        // FullSync already contains ItemState. Test the semantic bit directly so an
        // ObjectState-only update (for example a page flip) cannot re-run placement.
        bool appliesItemState = ItemUpdateData.IncludesItemState(snapshot.UpdateType);
        bool probeWorldState = DebugRuntime.EnabledFor("item") && appliesItemState && (snapshot.ItemState is ItemState.Dropped or ItemState.Thrown);
        string parentBefore = probeWorldState ? ParentPath() : string.Empty;
        using IDisposable debugScope = DebugTrace.BeginApply("item", "item.snapshot-apply", NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            "Item", snapshot.ItemNetId.ToString(), () => DebugApplyState(snapshot));
        Multiplayer.LogDebug(() => $"NetworkedItem.ApplySnapshot([netId: {snapshot?.ItemNetId}, ItemUpdateType: {snapshot?.UpdateType}, ItemState: {snapshot?.ItemState}, PlayerId: {snapshot?.PlayerId}, Active state: {gameObject.activeInHierarchy}])");

        if (appliesItemState)
        {
            PrepareForStateChange(snapshot.PlayerId, snapshot.ItemState);

            switch (snapshot.ItemState)
            {
                case ItemState.Dropped:
                case ItemState.Thrown:
                    HandleDroppedOrThrownState(snapshot);
                    break;

                case ItemState.InHand:
                case ItemState.InInventory:
                    HandleInventoryOrHandState(snapshot);
                    break;

                case ItemState.Attached:
                    HandleAttachedState(snapshot);
                    break;
                case ItemState.Removed:
                    Item.GetComponent<GadgetBase>()?.Remove(true);
                    break;

                default:
                    throw new Exception($"NetworkedItem.ApplySnapshot() Item state not implemented: {snapshot?.ItemState}");
            }
        }

        Multiplayer.LogDebug(() => $"NetworkedItem.ApplySnapshot() netID: {snapshot?.ItemNetId}, ItemUpdateType {snapshot?.UpdateType} About to process states");

        if (snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) || snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ObjectState))
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.ApplySnapshot() netID: {snapshot?.ItemNetId}, States: {snapshot?.States?.Count}");
            if (trackedValues.Count > 0 && snapshot.States != null)
            {
                ApplyTrackedValues(snapshot.States);
            }
        }

        Multiplayer.LogDebug(() => $"NetworkedItem.ApplySnapshot() netID: {snapshot?.ItemNetId}, ItemUpdateType {snapshot?.UpdateType} states processed");

        //mark values as clean
        if (appliesItemState)
        {
            // A received throw invokes GrabHandlerItem.Throw and then immediately becomes
            // an ungrabbed world item locally. Baseline it as Dropped so this client (or a
            // listen host) cannot echo a synthetic Dropped update on the following frame.
            lastState = ObservationBaselineAfterSnapshot(snapshot.ItemState);
            lastSentProjection = ItemWireStateComparer.StableBaselineAfterSend(
                ProjectionFromSnapshot(snapshot));
            hasLastSentState = true;
        }
        createdDirty = false;
        stateDirty = false;

        MarkValuesClean();
        hostStateObservationPending = false;
        EntityDebugRegistry.UpdateState("Item", NetId.ToString(), EntityDebugRegistry.ItemState(this));
        if (appliesItemState)
            PublishHolderInvariant(snapshot);

        if (probeWorldState)
        {
            PublishParentChange(parentBefore, "apply");
            PublishWorldStateInvariantWarning("apply.after", snapshot.ItemState);
            StartCoroutine(TracePostApply(snapshot.ItemState, ParentPath()));
        }
    }

    public ItemUpdateData CreateUpdateData(ItemUpdateData.ItemUpdateType updateType)
    {
        if (transform == null || Item == null || Item?.InventorySpecs == null || Item?.InventorySpecs?.ItemPrefabName == null)
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.CreateUpdateData({updateType}) NetId: {NetId}, name: {name}. Transform is null: {transform == null}, Item is null: {Item == null}, Inventory Specs: {Item?.InventorySpecs == null}, ItemPrefabName is null: {Item?.InventorySpecs?.ItemPrefabName == null}");
            return null;
        }

        Vector3 position;
        Quaternion rotation;
        Dictionary<string, object> states;
        ushort carId = 0;
        bool frontCoupler = true;

        if (wasThrown)
        {
            position = thrownPosition;
            rotation = thrownRotation;
        }
        else
        {
            position = transform.position - WorldMover.currentMove;
            rotation = transform.rotation;
        }

        if (updateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) || updateType.HasFlag(ItemUpdateData.ItemUpdateType.FullSync))
        {
            states = GetAllStateData();
        }
        else
        {
            states = GetDirtyStateData();
        }

        if (lastState == ItemState.Attached)
        {
            ItemSnapPointCoupler itemSnapPointCoupler = snappableItem.SnappedTo as ItemSnapPointCoupler;

            if (itemSnapPointCoupler != null)
            {
                carId = itemSnapPointCoupler.Car.GetNetId();
                frontCoupler = itemSnapPointCoupler.IsFront;
            }
        }

        var updateData = new ItemUpdateData
        {
            UpdateType = updateType,
            ItemNetId = NetId,
            PrefabName = Item.InventorySpecs.ItemPrefabName,
            ItemState = lastState,
            ItemPosition = position,
            ItemRotation = rotation,
            ThrowDirection = throwDirection,
            CarNetId = carId,
            AttachedFront = frontCoupler,
            States = states,
            PlayerId = playerBelongsToId,
            AuthorityRevision = AuthorityRevision,
            PersistentOwnerPlayerId = PersistentOwnerPlayerId,
            InventoryClaimPlayerId = InventoryClaimPlayerId,
            InventoryClaimSlot = InventoryClaimSlot,
            InventoryClaimFlags = InventoryClaimFlags,
            TransitionReason = LastTransitionReason
        };

        CaptureLocalInventoryClaim(updateData);

        Dictionary<string, object> snapshotDebugData = DebugTrace.ItemSnapshotData(updateData);
        if (DebugRuntime.EnabledFor("item"))
            snapshotDebugData["sourceUnityState"] = DebugUnityState();
        DebugRuntime.Publish("item", "item.snapshot-created", NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: NetId.ToString(), data: snapshotDebugData);

        return updateData;
    }

    private ItemState GetItemState()
    {
        //Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, Parent: {Item.transform.parent} WorldMover: {WorldMover.OriginShiftParent}, wasThrown: {wasThrown}, isGrabbed: {Item.IsGrabbed()} Inventory.Contains(): {Inventory.Instance.Contains(this.gameObject, false)} Storage.Contains: {StorageController.Instance.StorageInventory.ContainsItem(Item)}");


        if (wasThrown)
        {
            //Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, Parent: {Item.transform.parent} WorldMover: {WorldMover.OriginShiftParent}, wasThrown: {wasThrown}");
            return ItemState.Thrown;
        }

        if (Item.IsGrabbed())
            return ItemState.InHand;

        if (playerBelongsTo?.RightHandItemGO == gameObject)
            return ItemState.InHand;

        // A server has no local base-game inventory representation for a remote client, so
        // preserve that client's accepted placement until it sends another transition. The
        // host's own items must be observed from the host Inventory; preserving them here
        // hides InHand -> InInventory when the host switches equipped items.
        byte hostPlayerId = NetworkLifecycle.Instance.Server?.SelfId ?? 0;
        if (ItemStateObservationPolicy.PreserveRemotePlayerPlacement(
                NetworkLifecycle.Instance.IsHost(), BelongsTo?.PlayerId ?? 0,
                hostPlayerId, ToWireState(lastState)))
            return lastState;

        try
        {
            if (Inventory.Instance?.Contains(gameObject, false) == true ||
                StorageController.Instance?.StorageInventory?.ContainsItem(Item) == true)
                return ItemState.InInventory;
        }
        catch { }

        if (snappableItem != null && snappableItem.IsSnapped)
        {
            //Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, snapped! {this.transform.parent}");
            return ItemState.Attached;
        }

        if (wasRemoved)
        {
            //Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, was removed");
            return ItemState.Removed;
        }

        //do we need a condition to check if it's attached to something else (last attach vs current attach)?
        return ItemState.Dropped;
    }

    private bool IsRemotePlayerAuthoritative()
    {
        if (NetworkLifecycle.Instance.IsHost())
        {
            byte hostPlayerId = NetworkLifecycle.Instance.Server?.SelfId ?? 0;
            return BelongsTo != null && BelongsTo.PlayerId != hostPlayerId;
        }

        byte localPlayerId = NetworkLifecycle.Instance.Client?.PlayerId ?? 0;
        return playerBelongsToId != 0 && playerBelongsToId != localPlayerId;
    }

    private void ApplyTrackedValues(Dictionary<string, object> newValues)
    {
        Multiplayer.LogDebug(() => $"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Null checks");

        if (newValues == null || newValues.Count == 0)
            return;

        Multiplayer.LogDebug(() => $"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Registration complete: {registrationComplete}");

        TrackedStateMergePlan mergePlan = TrackedStateComposer.PlanMerge(
            GetTrackedStateEntries(), newValues, NetworkLifecycle.Instance.IsHost());

        foreach (var newValue in mergePlan.ApplicableValues)
        {
            var trackedValue = trackedValues.Find(tv => ((dynamic)tv).Key == newValue.Key);
            try
            {
                ((dynamic)trackedValue).SetValueFromObject(newValue.Value);
                Multiplayer.LogDebug(() => $"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}, Updated tracked value: {newValue.Key}, value: {newValue.Value} ");
            }
            catch (Exception ex)
            {
                Multiplayer.LogError($"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Error updating tracked value {newValue.Key}: {ex.Message}");
            }
        }

        foreach (string key in mergePlan.AuthorityRejectedKeys)
            Multiplayer.LogWarning($"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Skipped server-authoritative value update from client: {key}");
        foreach (string key in mergePlan.UnknownKeys)
            Multiplayer.LogWarning($"Tracked value not found: {key}\r\n {String.Join(", ", trackedValues.Select(val => ((dynamic)val).Key))}");
    }

    #region Item State Update Handlers

    private void PrepareForStateChange(byte playerId, ItemState targetState)
    {
        // Cleanup from previous state/desyncs
        if (Item.IsSnapped)
            Item.SnappableItem.SnappedTo.UnsnapItem(false); //Todo: should this be forced?

        if (playerBelongsTo != null && playerBelongsTo.PlayerId != playerId)
        {
            playerBelongsTo.DropItem(gameObject);
        }

        // Recover from stale ownership bookkeeping by checking the actual remote hand objects.
        if (playerId == 0 && NetworkLifecycle.Instance.IsClientRunning)
            foreach (NetworkedPlayer player in NetworkLifecycle.Instance.Client.ClientPlayerManager.Players)
                player?.DropItem(gameObject);

        // find new player reference
        playerBelongsToId = playerId;
        playerBelongsTo = null;

        byte localPlayerId = NetworkLifecycle.Instance.Client?.PlayerId ?? 0;
        if (playerId != localPlayerId)
        {
            try
            {
                Inventory inventory = Inventory.Instance;
                bool preserveOwnerClaim = Item?.IsEssential() == true &&
                    PersistentOwnerPlayerId != 0 && PersistentOwnerPlayerId == localPlayerId &&
                    InventoryClaimSlot >= 0;
                // Contains(includeDropped: true) also includes the persistent owner's
                // reserved recall silhouette. That is a claim, not active possession.
                // Treating it as possession caused every foreign snapshot to revoke the
                // owner again and made the local observed state oscillate.
                bool hadLocalPossession = Item?.IsGrabbed() == true ||
                    inventory?.Contains(gameObject, false) == true;
                grabHandler?.ForceEndInteraction();
                if (preserveOwnerClaim)
                {
                    // The same Unity object backs the reserved silhouette and the remote
                    // world/hand representation. Convert active local inventory state into
                    // a dropped reserved claim before projecting the foreign holder.
                    EnsureLocalOwnerDroppedClaim(inventory, targetState);
                }
                else if (inventory != null &&
                         (inventory.Contains(gameObject, true) || inventory.IndexOf(gameObject) >= 0))
                {
                    inventory.ItemContainerRegistry?.PurgeItemFromContainer(gameObject);
                    inventory.PurgeFromInventory(gameObject);
                }
                if (hadLocalPossession)
                {
                    DebugRuntime.Publish("item", "item.local-possession-revoked",
                        NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
                        entityType: "Item", entityId: NetId.ToString(), data: new()
                        {
                            ["localPlayerId"] = localPlayerId,
                            ["newPlacementPlayerId"] = playerId,
                            ["persistentOwnerPlayerId"] = PersistentOwnerPlayerId,
                            ["authorityRevision"] = AuthorityRevision
                        });
                }
            }
            catch (Exception exception)
            {
                Multiplayer.LogWarning($"Unable to revoke local possession for item {NetId}: {exception.Message}");
            }
        }
        if (playerId != 0 && playerId != localPlayerId && NetworkLifecycle.Instance.IsClientRunning)
            if (!NetworkLifecycle.Instance.Client.ClientPlayerManager.TryGetPlayer(playerId, out playerBelongsTo))
            {
                DebugDiagnostics.ReportDependency("Item", NetId.ToString(), "missing-player", $"playerId={playerId}");
                Multiplayer.LogWarning($"Unable to find player {playerId} for item {NetId}");
            }
            else DebugDiagnostics.ResolveDependency("Item", NetId.ToString(), "missing-player");
    }

    private void HandleDroppedOrThrownState(ItemUpdateData snapshot)
    {
        Multiplayer.LogDebug(() => $"NetworkedItem.HandleDroppedOrThrownState([netId: {snapshot?.ItemNetId}, ItemUpdateType: {snapshot?.UpdateType}, ItemState: {snapshot?.ItemState}, PlayerId: {snapshot?.PlayerId}, Active state: {gameObject.activeInHierarchy}])");

        DetachForWorldState(snapshot.ItemState);

        // Inventory-created canonical objects can retain their inactive presentation, inventory
        // layer and kinematic physics after detachment. Normalize the whole object before applying
        // the authoritative world transform; do not route through the host player's drop position.
        if (Item?.InventorySpecs != null)
            Item.InventorySpecs.BelongsToPlayer = PersistentOwnerPlayerId != 0;
        gameObject.SetActive(true);
        SetWorldPresentation();
        grabHandler?.TogglePhysics(true);
        ParentToWorld();
        transform.position = snapshot.ItemPosition + WorldMover.currentMove;
        transform.rotation = snapshot.ItemRotation;
        if (Item.ItemRigidbody != null)
        {
            Item.ItemRigidbody.isKinematic = false;
            Item.ItemRigidbody.velocity = Vector3.zero;
            Item.ItemRigidbody.angularVelocity = Vector3.zero;
        }

        //handle throwing of the item
        if (snapshot.ItemState == ItemState.Thrown)
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.HandleDroppedOrThrownState() ItemNetId: {snapshot?.ItemNetId} Thrown. Position: {transform.position}, Direction: {snapshot?.ThrowDirection}");

            wasThrown = true;
            grabHandler?.Throw(snapshot.ThrowDirection);
            // OnThrow consumes this guard when the Harmony prefix observes the call. Clear
            // it explicitly as well for item types without a GrabHandlerItem/throw callback.
            wasThrown = false;
            gameObject.SetActive(true);
            SetWorldPresentation();
            ParentToWorld();
        }
        else
        {
            wasThrown = false;
            Multiplayer.LogDebug(() => $"NetworkedItem.HandleDroppedOrThrownState() ItemNetId: {snapshot?.ItemNetId} Dropped. Position: {transform.position}");
        }
    }

    private static ItemState ObservationBaselineAfterSnapshot(ItemState state)
    {
        return state == ItemState.Thrown ? ItemState.Dropped : state;
    }

    private Dictionary<string, object> DebugApplyState(ItemUpdateData snapshot)
    {
        Dictionary<string, object> state = DebugUnityState();
        state["updateType"] = snapshot?.UpdateType.ToString() ?? string.Empty;
        state["authorityRevision"] = snapshot?.AuthorityRevision ?? 0;
        state["stateFingerprint"] = snapshot?.DebugCorrelationFingerprint ?? string.Empty;
        return state;
    }

    private bool HasNetworkStateProjectionChanged(ItemState currentState)
    {
        return ItemWireStateComparer.RequiresSend(hasLastSentState, lastSentProjection,
            CurrentWireProjection(currentState));
    }

    private ItemWireStateProjection CurrentWireProjection(ItemState state)
    {
        ushort carNetId = 0;
        bool attachedFront = false;
        if (state == ItemState.Attached && snappableItem?.SnappedTo is ItemSnapPointCoupler coupler)
        {
            carNetId = coupler.Car.GetNetId();
            attachedFront = coupler.IsFront;
        }
        return new ItemWireStateProjection(ToWireState(state), playerBelongsToId,
            carNetId, attachedFront);
    }

    private static ItemWireStateProjection ProjectionFromSnapshot(ItemUpdateData snapshot) =>
        new(ToWireState(snapshot.ItemState), snapshot.PlayerId, snapshot.CarNetId, snapshot.AttachedFront);

    private static WireItemState ToWireState(ItemState state) => state switch
    {
        ItemState.Dropped => WireItemState.Dropped,
        ItemState.Thrown => WireItemState.Thrown,
        ItemState.InInventory => WireItemState.InInventory,
        ItemState.InHand => WireItemState.InHand,
        ItemState.Attached => WireItemState.Attached,
        ItemState.Removed => WireItemState.Removed,
        _ => WireItemState.Dropped
    };

    private void DetachForWorldState(ItemState targetState)
    {
        PublishUnityState("item.detach.before", new() { ["targetState"] = targetState.ToString() });
        PublishUnityState("item.hand-membership.before", new() { ["targetState"] = targetState.ToString() });
        string parentBefore = ParentPath();

        if (playerBelongsTo != null)
            playerBelongsTo.DropItem(gameObject);
        if (NetworkLifecycle.Instance.IsClientRunning)
            foreach (NetworkedPlayer player in NetworkLifecycle.Instance.Client.ClientPlayerManager.Players)
                player?.DropItem(gameObject);

        try
        {
            Inventory inventory = Inventory.Instance;
            grabHandler?.ForceEndInteraction();
            inventory?.ItemContainerRegistry?.PurgeItemFromContainer(gameObject);
            if (ShouldPreserveLocalOwnerClaim())
                EnsureLocalOwnerDroppedClaim(inventory, targetState);
            else
                inventory?.PurgeFromInventory(gameObject);
        }
        catch (Exception exception)
        {
            Multiplayer.LogWarning($"NetworkedItem.DetachForWorldState() inventory cleanup failed for {NetId}: {exception.Message}");
        }

        try
        {
            StorageController storage = StorageController.Instance;
            StorageTransitionPlan plan = StorageTransitionPlanner.Plan(
                new UnityStorageView(storage, Item), StorageMembership.World);
            if (!plan.Accepted)
            {
                DebugRuntime.Publish("storage", "storage.transition-rejected",
                    NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
                    DebugSeverity.Warning, "Item", NetId.ToString(), new()
                    {
                        ["reason"] = plan.Reason,
                        ["target"] = StorageMembership.World.ToString()
                    });
            }
            else
            {
                if (plan.Remove.HasFlag(StorageMembership.ItemContainer))
                    storage.RemoveItemFromStorageItemContainers(Item);
                if (plan.Remove.HasFlag(StorageMembership.Inventory))
                    storage.RemoveItemFromStorageItemList(storage.StorageInventory, Item);
                if (plan.Remove.HasFlag(StorageMembership.LostAndFound))
                    storage.RemoveItemFromStorageItemList(storage.StorageLostAndFound, Item);
                if (plan.Remove.HasFlag(StorageMembership.World))
                    storage.RemoveItemFromStorageItemList(storage.StorageWorld, Item);
                if (plan.Add.HasFlag(StorageMembership.World))
                    storage.AddItemToWorldStorage(Item);
                if (plan.RepairedMultipleMembership)
                    DebugRuntime.Publish("storage", "storage.multiple-membership-repaired",
                        NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
                        DebugSeverity.Warning, "Item", NetId.ToString(), new()
                        {
                            ["removed"] = plan.Remove.ToString(),
                            ["target"] = StorageMembership.World.ToString()
                        });
            }
        }
        catch (Exception exception)
        {
            Multiplayer.LogWarning($"NetworkedItem.DetachForWorldState() storage cleanup failed for {NetId}: {exception.Message}");
        }

        playerBelongsToId = 0;
        playerBelongsTo = null;
        ParentToWorld();
        PublishParentChange(parentBefore, "detach");
        PublishUnityState("item.hand-membership.after", new() { ["targetState"] = targetState.ToString() });
        PublishUnityState("item.detach.after", new() { ["targetState"] = targetState.ToString() });
    }

    private bool ShouldPreserveLocalOwnerClaim()
    {
        byte localPlayerId = NetworkLifecycle.Instance?.Client?.PlayerId ?? 0;
        return Item?.IsEssential() == true && localPlayerId != 0 &&
            PersistentOwnerPlayerId == localPlayerId && InventoryClaimSlot >= 0;
    }

    private void EnsureLocalOwnerDroppedClaim(Inventory inventory, ItemState targetState)
    {
        UnityInventoryView view = new(inventory, gameObject, InventoryClaimSlot);
        InventoryClaimPlan plan = InventoryClaimPlanner.EnsureDroppedClaim(view, InventoryClaimSlot);
        if (plan.Rejected)
        {
            PublishClaimInvariant(plan.Reason, plan.TargetSlot, targetState);
            return;
        }

        switch (plan.Action)
        {
            case InventoryClaimAction.None:
                return;
            case InventoryClaimAction.DropExistingItemInPlace:
                if (inventory.DropItemFromHandsOrInventory(gameObject) == null)
                    PublishClaimInvariant("essential-claim-drop-failed", plan.TargetSlot, targetState);
                return;
            case InventoryClaimAction.AddToExpectedSlotThenDrop:
                if (inventory.AddItemToInventory(gameObject, plan.TargetSlot, false) < 0 ||
                    inventory.DropItemFromHandsOrInventory(gameObject) == null)
                {
                    PublishClaimInvariant("essential-claim-repair-failed", plan.TargetSlot, targetState);
                    return;
                }
                break;
            default:
                PublishClaimInvariant("essential-claim-plan-invalid", plan.TargetSlot, targetState);
                return;
        }

        DebugRuntime.Publish("inventory", "inventory.essential-claim-repaired",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: NetId.ToString(), data: new()
            {
                ["slot"] = InventoryClaimSlot,
                ["targetState"] = targetState.ToString(),
                ["persistentOwnerPlayerId"] = PersistentOwnerPlayerId
            });
    }

    private void PublishClaimInvariant(string code, int actualSlot, ItemState targetState)
    {
        DebugRuntime.Publish("inventory", "inventory.essential-claim-invariant-violation",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            DebugSeverity.Warning, "Item", NetId.ToString(), new()
            {
                ["code"] = code,
                ["expectedSlot"] = InventoryClaimSlot,
                ["actualSlot"] = actualSlot,
                ["targetState"] = targetState.ToString(),
                ["claimFlags"] = InventoryClaimFlags.ToString()
            });
    }

    private void ParentToWorld()
    {
        Transform worldParent = WorldMover.OriginShiftParent;
        ItemReparentingBase reparenting = GetComponent<ItemReparentingBase>();
        reparenting?.ParentItemExternal(worldParent, null, null);
        if (transform.parent != worldParent)
            transform.SetParent(worldParent, true);
    }

    private void SetWorldPresentation()
    {
        Dictionary<string, int> childLayersBefore = DebugRuntime.EnabledFor("item") ? ChildLayerHistogram() : null;
        int previousRootLayer = gameObject.layer;
        int worldLayer = LayerMask.NameToLayer("World_Item");
        if (worldLayer >= 0) gameObject.layer = worldLayer;

        // Do not blanket-enable child renderers here. Documents, maps and other
        // multi-state items intentionally keep alternate page/cover meshes disabled;
        // enabling every renderer produces the opaque white sheet seen on clients.
        // Child layers are equally item-specific: dropped maps keep their touchscreens
        // and previous/next buttons on Inventory while their root/pages use World_Item.
        // Only the root presentation layer is canonical for world-state validation.
        if (childLayersBefore != null)
            DebugRuntime.Publish("item", "item.world-presentation-layer-applied",
                NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
                entityType: "Item", entityId: NetId.ToString(), data: new()
                {
                    ["previousRootLayer"] = previousRootLayer,
                    ["previousRootLayerName"] = LayerMask.LayerToName(previousRootLayer) ?? string.Empty,
                    ["currentRootLayer"] = gameObject.layer,
                    ["currentRootLayerName"] = LayerMask.LayerToName(gameObject.layer) ?? string.Empty,
                    ["descendantLayersBefore"] = childLayersBefore,
                    ["descendantLayersAfter"] = ChildLayerHistogram(),
                    ["descendantLayersPreserved"] = true
                });
    }

    private Dictionary<string, int> ChildLayerHistogram()
    {
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        foreach (Transform child in GetComponentsInChildren<Transform>(true))
        {
            if (child == transform) continue;
            int layer = child.gameObject.layer;
            string name = LayerMask.LayerToName(layer);
            string key = string.IsNullOrEmpty(name) ? layer.ToString() : $"{layer}:{name}";
            result[key] = result.TryGetValue(key, out int count) ? count + 1 : 1;
        }
        return result;
    }

    private IEnumerator TracePostApply(ItemState expectedState, string expectedParent)
    {
        yield return null;
        PublishUnityState("item.post-apply.frame+1", new() { ["expectedState"] = expectedState.ToString() });
        PublishParentChange(expectedParent, "frame+1");
        PublishWorldStateInvariantWarning("frame+1", expectedState);

        for (int frame = 1; frame < 5; frame++)
            yield return null;

        PublishUnityState("item.post-apply.frame+5", new() { ["expectedState"] = expectedState.ToString() });
        PublishParentChange(expectedParent, "frame+5");
        PublishWorldStateInvariantWarning("frame+5", expectedState);
    }

    private void PublishWorldStateInvariantWarning(string stage, ItemState expectedState)
    {
        List<string> violations = [];
        int worldLayer = LayerMask.NameToLayer("World_Item");
        int inventoryLayer = LayerMask.NameToLayer("Inventory");
        if (!gameObject.activeSelf)
            violations.Add("inactive-self");
        if (!gameObject.activeInHierarchy)
            violations.Add("inactive-in-hierarchy");
        if (inventoryLayer >= 0 && gameObject.layer == inventoryLayer)
            violations.Add("inventory-layer");
        else if (worldLayer >= 0 && gameObject.layer != worldLayer)
            violations.Add("not-world-item-layer");
        if (Item?.ItemRigidbody?.isKinematic == true)
            violations.Add("rigidbody-kinematic");
        if (transform.parent != WorldMover.OriginShiftParent)
            violations.Add("wrong-world-parent");

        try
        {
            Inventory inventory = Inventory.Instance;
            if (inventory?.Contains(gameObject, false) == true)
                violations.Add("inventory-member");
            var container = inventory?.ItemContainerRegistry?.GetItemContainerAndIndex(gameObject);
            if (container.HasValue && container.Value.Item1 != null)
                violations.Add("item-container-member");
            if (StorageController.Instance?.StorageInventory?.ContainsItem(Item) == true)
                violations.Add("storage-inventory-member");
        }
        catch (Exception exception)
        {
            violations.Add("membership-probe-error:" + exception.GetType().Name);
        }

        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers.Length > 0 && !renderers.Any(renderer => renderer.enabled))
            violations.Add("no-enabled-renderer");

        if (violations.Count == 0)
            return;

        Dictionary<string, object> data = DebugUnityState();
        data["stage"] = stage;
        data["expectedState"] = expectedState.ToString();
        data["violations"] = violations.ToArray();
        Multiplayer.LogWarning($"World item {NetId} failed {stage} invariants: {string.Join(", ", violations)}");
        DebugRuntime.Publish("item", "item.world-state-invariant-violation",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            DebugSeverity.Warning, "Item", NetId.ToString(), data);
    }

    private void PublishParentChange(string previousParent, string stage)
    {
        string currentParent = ParentPath();
        if (string.Equals(previousParent, currentParent, StringComparison.Ordinal))
            return;
        PublishUnityState("item.parent-changed", new()
        {
            ["stage"] = stage,
            ["previousParent"] = previousParent,
            ["currentParent"] = currentParent
        });
    }

    private void PublishUnityState(string eventName, Dictionary<string, object> additional)
    {
        if (!DebugRuntime.EnabledFor("item"))
            return;
        Dictionary<string, object> data = DebugUnityState();
        if (additional != null)
            foreach (KeyValuePair<string, object> value in additional)
                data[value.Key] = value.Value;
        DebugRuntime.Publish("item", eventName, NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: NetId.ToString(), data: data);
    }

    internal Dictionary<string, object> DebugUnityState()
    {
        Dictionary<string, object> state = new(StringComparer.Ordinal)
        {
            ["lastState"] = lastState.ToString(),
            ["computedState"] = GetItemState().ToString(),
            ["isGrabbed"] = Item?.IsGrabbed() ?? false,
            ["parent"] = ParentPath(),
            ["gameObjectPath"] = gameObject.GetPath(),
            ["activeSelf"] = gameObject.activeSelf,
            ["activeInHierarchy"] = gameObject.activeInHierarchy,
            ["layer"] = gameObject.layer,
            ["layerName"] = LayerMask.LayerToName(gameObject.layer) ?? string.Empty,
            ["remoteHolderPlayerId"] = playerBelongsTo?.PlayerId ?? 0,
            ["remoteHandMember"] = playerBelongsTo?.RightHandItemGO == gameObject,
            ["placementPlayerId"] = playerBelongsToId,
            ["hostPossessorPlayerId"] = BelongsTo?.PlayerId ?? 0,
            ["authorityRevision"] = AuthorityRevision,
            ["persistentOwnerPlayerId"] = PersistentOwnerPlayerId,
            ["inventoryClaimPlayerId"] = InventoryClaimPlayerId,
            ["inventoryClaimSlot"] = InventoryClaimSlot,
            ["inventoryClaimFlags"] = InventoryClaimFlags.ToString(),
            ["transitionReason"] = LastTransitionReason.ToString(),
            ["foreignOwned"] = IsForeignOwned,
            ["position"] = DebugValueSnapshotter.Snapshot(transform.position),
            ["positionAbsolute"] = DebugValueSnapshotter.Snapshot(transform.position - WorldMover.currentMove),
            ["rotation"] = DebugValueSnapshotter.Snapshot(transform.rotation),
            ["grabHandlerPresent"] = grabHandler != null,
            ["grabInteractionAllowed"] = grabHandler?.interactionAllowed ?? false
        };

        try
        {
            NetworkedPlayer actualRemoteHolder = NetworkLifecycle.Instance.IsClientRunning
                ? NetworkLifecycle.Instance.Client.ClientPlayerManager.Players.FirstOrDefault(player => player?.RightHandItemGO == gameObject)
                : null;
            state["actualRemoteHandMember"] = actualRemoteHolder != null;
            state["actualRemoteHolderPlayerId"] = actualRemoteHolder?.PlayerId ?? 0;
            state["actualRemoteHolderPath"] = actualRemoteHolder?.gameObject?.GetPath() ?? string.Empty;
        }
        catch (Exception exception)
        {
            state["remoteHandProbeError"] = exception.Message;
        }

        Rigidbody rigidbody = Item?.ItemRigidbody;
        state["rigidbodyIsKinematic"] = rigidbody?.isKinematic ?? false;
        state["velocity"] = DebugValueSnapshotter.Snapshot(rigidbody?.velocity);
        state["angularVelocity"] = DebugValueSnapshotter.Snapshot(rigidbody?.angularVelocity);

        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        int enabledRenderers = renderers.Count(renderer => renderer.enabled);
        state["rendererCount"] = renderers.Length;
        state["rendererEnabledCount"] = enabledRenderers;
        state["anyRendererEnabled"] = enabledRenderers > 0;
        state["allRenderersEnabled"] = renderers.Length == 0 || enabledRenderers == renderers.Length;

        PageBook pageBook = GetComponentInChildren<PageBook>(true);
        if (pageBook != null)
        {
            state["pageBookCurrentPage"] = pageBook.currentPage;
            state["pageBookPageCount"] = pageBook.PageNum;
            state["pageBookPagesGenerated"] = pageBook.PagesGenerated;
            state["pageBookRuntimePageCount"] = pageBook.pages?.Count ?? 0;
            NetworkedPageBookState pageState = GetComponent<NetworkedPageBookState>();
            state["pageBookLogicalPage"] = pageState?.LogicalPage ?? pageBook.currentPage;
            state["pageBookPendingPage"] = pageState?.PendingPage;
        }

        ItemReparentingBase reparenting = GetComponent<ItemReparentingBase>();
        state["reparentingCurrentParent"] = reparenting?.CurrentParent?.gameObject?.GetPath() ?? string.Empty;

        try
        {
            Inventory inventory = Inventory.Instance;
            state["inventoryMember"] = inventory?.Contains(gameObject, false) ?? false;
            var container = inventory?.ItemContainerRegistry?.GetItemContainerAndIndex(gameObject);
            state["itemContainerMember"] = container.HasValue && container.Value.Item1 != null;
            state["itemContainerType"] = container.HasValue ? container.Value.Item1?.GetType().Name ?? string.Empty : string.Empty;
            state["itemContainerIndex"] = container.HasValue ? container.Value.Item2 : -1;
        }
        catch (Exception exception)
        {
            state["inventoryProbeError"] = exception.Message;
        }

        try
        {
            StorageController storage = StorageController.Instance;
            state["storageInventoryMember"] = storage?.StorageInventory?.ContainsItem(Item) ?? false;
            state["storageWorldMember"] = storage?.StorageWorld?.ContainsItem(Item) ?? false;
            state["storageLostAndFoundMember"] = storage?.StorageLostAndFound?.ContainsItem(Item) ?? false;
            state["storageItemContainerMember"] = storage?.StorageItemContainers?.ContainsItem(Item) ?? false;
        }
        catch (Exception exception)
        {
            state["storageProbeError"] = exception.Message;
        }

        return state;
    }

    private string ParentPath() => transform.parent?.gameObject?.GetPath() ?? string.Empty;

    private void HandleAttachedState(ItemUpdateData snapshot)
    {
        //handle attaching the item
        gameObject.SetActive(true);
        Multiplayer.LogDebug(() => $"NetworkedItem.HandleAttachedState() ItemNetId: {snapshot?.ItemNetId} attempting attachment to car {snapshot.CarNetId}, at the front {snapshot.AttachedFront}");

        if (!NetworkedTrainCar.TryGet(snapshot.CarNetId, out TrainCar trainCar))
        {
            DebugDiagnostics.ReportDependency("Item", NetId.ToString(), "missing-train-car", $"carNetId={snapshot.CarNetId}");
            Multiplayer.LogWarning($"NetworkedItem.HandleAttachedState() CarNetId: {snapshot?.CarNetId} not found for ItemNetId: {snapshot?.ItemNetId}");
            return;
        }
        DebugDiagnostics.ResolveDependency("Item", NetId.ToString(), "missing-train-car");

        //Try to find the coupler snap point for the car and correct end to snap to
        var snapPoint = trainCar?.physicsLod?.GetCouplerSnapPoints()
            .FirstOrDefault(sp => sp.IsFront == snapshot.AttachedFront);

        if (snapPoint == null)
        {
            DebugDiagnostics.ReportDependency("Item", NetId.ToString(), "missing-snap-point", $"carNetId={snapshot.CarNetId} front={snapshot.AttachedFront}");
            Multiplayer.LogWarning($"NetworkedItem.HandleAttachedState() ItemNetId: {snapshot?.ItemNetId}. No valid snap point found for car {snapshot.CarNetId}");
            return;
        }
        DebugDiagnostics.ResolveDependency("Item", NetId.ToString(), "missing-snap-point");

        //Attempt attachment to car
        Item.ItemRigidbody.isKinematic = false;
        if (!snapPoint.SnapItem(Item, false))
        {
            DebugDiagnostics.ReportDependency("Item", NetId.ToString(), "attachment-failed", $"carNetId={snapshot.CarNetId} front={snapshot.AttachedFront}");
            Multiplayer.LogWarning($"NetworkedItem.HandleAttachedState() Attachment failed for item {snapshot?.ItemNetId} to car {snapshot.CarNetId}");
        }
        else DebugDiagnostics.ResolveDependency("Item", NetId.ToString(), "attachment-failed");
    }

    private void HandleInventoryOrHandState(ItemUpdateData snapshot)
    {
        Multiplayer.LogDebug(() => $"NetworkedItem.HandleInventoryOrHandState() ItemNetId: {snapshot?.ItemNetId} State: {snapshot?.ItemState}. Player: {snapshot?.PlayerId}, Position: {snapshot?.ItemPosition}");

        byte localPlayerId = NetworkLifecycle.Instance.Client?.PlayerId ?? 0;
        RemoteItemProjectionPlan plan = RemoteItemProjectionPlanner.Plan(
            ToWireState(snapshot.ItemState), snapshot.PlayerId, localPlayerId, playerBelongsTo != null);
        if (plan.ClearExistingRemoteHands && NetworkLifecycle.Instance.IsClientRunning)
            foreach (NetworkedPlayer player in NetworkLifecycle.Instance.Client.ClientPlayerManager.Players)
                player?.DropItem(gameObject);

        if (plan.Action is RemoteItemProjectionAction.LocalHand or
            RemoteItemProjectionAction.LocalInventory)
        {
            playerBelongsTo = null;
            playerBelongsToId = localPlayerId;

            if (plan.Action == RemoteItemProjectionAction.LocalInventory)
                RestoreLocalInventoryClaim(Inventory.Instance, snapshot);
            else if (plan.Activate)
                gameObject.SetActive(true);

            DebugRuntime.Publish("item", snapshot.TransitionReason == ItemTransitionReason.OwnerRecall
                    ? "item.recall-applied" : "item.local-authority-projection-applied",
                NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
                entityType: "Item", entityId: NetId.ToString(), data: DebugUnityState());
            return;
        }

        if (plan.Action == RemoteItemProjectionAction.RemoteHand)
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.HandleInventoryOrHandState() Giving to player {playerBelongsTo.DisplayName}");
            var anchorOffsets = grabHandler?.GetAnchorOffsets();
            Vector3? pos = null;
            Quaternion? rot = null;
            if (anchorOffsets.HasValue)
            {
                pos = anchorOffsets.Value.anchorPositionOffset;
                rot = anchorOffsets.Value.anchorRotationOffset;
            }
            // A player can only have one canonical right-hand representation. Clear any
            // previous item before installing this projection.
            playerBelongsTo.DropItem();
            playerBelongsTo.HoldItem(gameObject, pos, rot);
            gameObject.SetActive(true);
        }
        else if (plan.Action == RemoteItemProjectionAction.RemoteInventory)
        {
            playerBelongsTo.DropItem(gameObject);
            playerBelongsTo.AddItemToInventory(gameObject);
        }
        else
        {
            DebugDiagnostics.ReportDependency("Item", NetId.ToString(), "missing-player",
                $"playerId={snapshot.PlayerId} projection={plan.Action}");
            Multiplayer.LogWarning($"Could not project item {NetId} to player {snapshot.PlayerId}; disabling");
            if (plan.Deactivate)
                gameObject.SetActive(false);
        }
    }

    private void RestoreLocalInventoryClaim(Inventory inventory, ItemUpdateData snapshot)
    {
        int requestedSlot = snapshot.InventoryClaimSlot;
        UnityInventoryView view = new(inventory, gameObject, requestedSlot);
        InventoryClaimPlan plan = InventoryClaimPlanner.RestoreClaim(view, requestedSlot);
        if (plan.Rejected)
        {
            PublishClaimInvariant(plan.Reason, plan.TargetSlot, ItemState.InInventory);
            return;
        }

        if (plan.Action is InventoryClaimAction.RestoreExistingItem or
            InventoryClaimAction.AddToExpectedSlot)
        {
            // AddItemToInventory intentionally revives an existing dropped/reserved entry:
            // the base game locates its reserved slot and toggles IsDropped off in place.
            int restoredSlot = inventory.AddItemToInventory(gameObject, plan.TargetSlot, false);
            if (restoredSlot < 0)
            {
                PublishClaimInvariant("recall-claim-restore-failed", plan.TargetSlot, ItemState.InInventory);
                return;
            }
        }

        DebugRuntime.Publish("inventory", "inventory.essential-claim-restored",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: NetId.ToString(), data: new()
            {
                ["requestedSlot"] = requestedSlot,
                ["restoredSlot"] = inventory.IndexOf(gameObject),
                ["plan"] = plan.Action.ToString(),
                ["reserved"] = inventory.GetSlotReservedState(plan.TargetSlot),
                ["dropped"] = inventory.GetSlotDroppedState(plan.TargetSlot),
                ["persistentOwnerPlayerId"] = PersistentOwnerPlayerId,
                ["authorityRevision"] = AuthorityRevision
            });
    }

    private sealed class UnityInventoryView : IInventoryView
    {
        private readonly Inventory inventory;
        private readonly GameObject item;
        private readonly int expectedSlot;

        public UnityInventoryView(Inventory inventory, GameObject item, int expectedSlot)
        {
            this.inventory = inventory;
            this.item = item;
            this.expectedSlot = expectedSlot;
        }

        public bool IsAvailable => inventory != null;
        public int CurrentSlot => inventory?.IndexOf(item) ?? -1;
        public bool ContainsActiveItem => inventory?.Contains(item, false) == true;
        public bool IsExpectedSlotOccupiedByOther
        {
            get
            {
                if (inventory == null || expectedSlot < 0) return false;
                GameObject occupant = inventory.PeekItemAtSlot(expectedSlot, true);
                return occupant != null && occupant != item;
            }
        }
        public bool CurrentSlotReserved => CurrentSlot >= 0 && inventory.GetSlotReservedState(CurrentSlot);
        public bool CurrentSlotDropped => CurrentSlot >= 0 && inventory.GetSlotDroppedState(CurrentSlot);
    }

    private sealed class UnityStorageView : IStorageView
    {
        private readonly StorageController storage;
        private readonly ItemBase item;

        public UnityStorageView(StorageController storage, ItemBase item)
        {
            this.storage = storage;
            this.item = item;
        }

        public bool IsAvailable => storage != null && item != null;
        public StorageMembership Membership
        {
            get
            {
                if (!IsAvailable) return StorageMembership.None;
                StorageMembership membership = StorageMembership.None;
                if (storage.StorageInventory?.ContainsItem(item) == true)
                    membership |= StorageMembership.Inventory;
                if (storage.StorageWorld?.ContainsItem(item) == true)
                    membership |= StorageMembership.World;
                if (storage.StorageLostAndFound?.ContainsItem(item) == true)
                    membership |= StorageMembership.LostAndFound;
                if (storage.StorageItemContainers?.ContainsItem(item) == true)
                    membership |= StorageMembership.ItemContainer;
                return membership;
            }
        }
    }

    internal void ApplyAuthorityMetadata(ItemUpdateData snapshot)
    {
        if (snapshot == null)
            return;
        AuthorityRevision = snapshot.AuthorityRevision;
        PersistentOwnerPlayerId = snapshot.PersistentOwnerPlayerId;
        InventoryClaimPlayerId = snapshot.InventoryClaimPlayerId;
        InventoryClaimSlot = snapshot.InventoryClaimSlot;
        InventoryClaimFlags = snapshot.InventoryClaimFlags;
        LastTransitionReason = snapshot.TransitionReason;
    }

    private void PublishHolderInvariant(ItemUpdateData snapshot)
    {
        byte localPlayerId = NetworkLifecycle.Instance.Client?.PlayerId ?? 0;
        NetworkedPlayer actualRemoteHolder = null;
        if (NetworkLifecycle.Instance.IsClientRunning)
            actualRemoteHolder = NetworkLifecycle.Instance.Client.ClientPlayerManager.Players
                .FirstOrDefault(player => player?.RightHandItemGO == gameObject);
        List<string> violations = new();
        if (snapshot.ItemState == ItemState.InHand && snapshot.PlayerId != localPlayerId &&
            actualRemoteHolder?.PlayerId != snapshot.PlayerId)
            violations.Add("expected-remote-hand-missing");
        if (snapshot.ItemState != ItemState.InHand && actualRemoteHolder != null)
            violations.Add("stale-remote-hand-membership");
        if (NetworkLifecycle.Instance.IsHost() && (BelongsTo?.PlayerId ?? 0) != snapshot.PlayerId)
            violations.Add("host-possessor-mismatch");
        if (violations.Count == 0)
            return;
        DebugRuntime.Publish("item", "item.holder-invariant-violation",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            DebugSeverity.Warning, "Item", NetId.ToString(), new()
            {
                ["expectedState"] = snapshot.ItemState.ToString(),
                ["expectedPlayerId"] = snapshot.PlayerId,
                ["localPlayerId"] = localPlayerId,
                ["actualRemoteHolderPlayerId"] = actualRemoteHolder?.PlayerId ?? 0,
                ["hostPossessorPlayerId"] = BelongsTo?.PlayerId ?? 0,
                ["violations"] = violations
            });
    }

    private void CaptureLocalInventoryClaim(ItemUpdateData snapshot)
    {
        if (snapshot == null || Item?.IsEssential() != true)
            return;
        byte localPlayerId = NetworkLifecycle.Instance.Client?.PlayerId ?? 0;
        if (localPlayerId == 0 || PersistentOwnerPlayerId != 0 && PersistentOwnerPlayerId != localPlayerId)
            return;
        try
        {
            Inventory inventory = Inventory.Instance;
            int slot = inventory?.IndexOf(gameObject) ?? -1;
            if (slot < 0)
                return;
            snapshot.InventoryClaimPlayerId = localPlayerId;
            snapshot.InventoryClaimSlot = slot;
            ItemInventoryClaimFlags flags = ItemInventoryClaimFlags.None;
            if (inventory.GetSlotReservedState(slot)) flags |= ItemInventoryClaimFlags.Reserved;
            if (inventory.GetSlotLockState(slot)) flags |= ItemInventoryClaimFlags.Locked;
            if (inventory.GetSlotDroppedState(slot)) flags |= ItemInventoryClaimFlags.Dropped;
            snapshot.InventoryClaimFlags = flags;
        }
        catch (Exception exception)
        {
            Multiplayer.LogWarning($"Unable to capture inventory claim for item {NetId}: {exception.Message}");
        }
    }
    #endregion

    public string GetDirtyValuesDebugString()
    {
        var dirtyValues = trackedValues.Where(tv => ((dynamic)tv).IsDirty).ToList();
        if (dirtyValues.Count == 0)
        {
            return "No dirty values";
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Dirty values for NetworkedItem: {name}, NetId: {NetId}:");
        foreach (var value in dirtyValues)
        {
            sb.AppendLine(((dynamic)value).GetDebugString());
        }
        return sb.ToString();
    }
    #endregion
}
