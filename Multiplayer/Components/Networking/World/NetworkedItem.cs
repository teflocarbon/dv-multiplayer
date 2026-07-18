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
using Multiplayer.Integrations.Inventory;
using Multiplayer.Integrations.Storage;
using Multiplayer.Core.Collections;
using Multiplayer.Core.Items;
using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Components.Networking.World.WorldItems;

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

/// <summary>Unity lifecycle, registration, authored identity, and local state observation.</summary>
public partial class NetworkedItem : IdMonoBehaviour<ushort, NetworkedItem>
{
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

    public ServerPlayer BelongsTo { get; private set; }

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
    private bool recallPreparationPending;
    private uint recallPreparationOperationId;
    private bool hostStateObservationPending;
    private bool clientBindingGateApplied;
    private bool placementSnapshotPending;
    private bool coldStorageRetirement;
    private bool coldMaterializationPending;
    private bool interactionAllowedBeforeBindingGate;
    private readonly ItemOutboundRevisionPipeline outboundRevisionPipeline = new();
    internal ClientItemUnboundState UnboundState { get; private set; }

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
    public bool IsForeignOwned => InventoryIntegration.Project(this).ForeignOwned;
    public string AuthoredItemKey { get; private set; }
    public bool IsSceneAuthored => WorldItemStableIdentity.IsValid(AuthoredItemKey);
    internal bool IsReplenishableAuthoredOfficeSlot { get; private set; }
    internal bool IsAuthoredShopSlot { get; private set; }

    protected override bool IsIdServerAuthoritative => true;

    protected override void Awake()
    {
        // Capture this before base/DV lifecycle work can move the object from its authored
        // content hierarchy to origin_shift_parent.
        if (TryGetComponent(out ItemBase authoredItem) && WorldItemStableIdentity.IsLikelyAuthoredAtAwake(authoredItem))
        {
            AuthoredItemKey = WorldItemStableIdentity.Capture(authoredItem);
            string authoredPath = WorldItemStableIdentity.HierarchyPath(transform);
            IsReplenishableAuthoredOfficeSlot = authoredPath.IndexOf("/Offices/",
                StringComparison.OrdinalIgnoreCase) >= 0;
            IsAuthoredShopSlot = GetComponentsInParent<MonoBehaviour>(true).Any(component =>
                component != null && (component.GetType().Namespace ?? string.Empty)
                    .StartsWith("DV.Shops", StringComparison.Ordinal));
        }
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
        if (coldMaterializationPending)
            return;
        if (!NetworkLifecycle.Instance.IsHost() &&
            NetworkedItemManager.Instance?.IsClientLostAndFoundTombstoned(NetId) == true)
        {
            EnforceClientLostAndFoundProjection();
            return;
        }
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

        if (!suppressNetworkDestroy && NetworkLifecycle.Instance.IsHost() &&
            !coldStorageRetirement && !coldMaterializationPending)
        {
            WorldItemPersistenceManager.RecordDestroyed(this);
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
        if (NetworkLifecycle.Instance.IsHost())
            JobReportArtifactRegistry.Remove(NetId);
        base.OnDestroy();
    }

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
            if (NetworkLifecycle.Instance.IsHost())
                NetworkedItemManager.Instance?.RegisterHostWorldItem(this);
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

    internal void EnsureAuthoredItemKey()
    {
        // Never promote an after-load dynamic materialization into the authored catalogue.
        // The only valid source is the content-scene staging hierarchy observed during Awake.
        if (!WorldItemStableIdentity.IsValid(AuthoredItemKey) && Item != null &&
            WorldItemStableIdentity.IsLikelyAuthoredAtAwake(Item))
            AuthoredItemKey = WorldItemStableIdentity.Capture(Item);
        if (NetworkLifecycle.Instance.IsHost() && IsSceneAuthored)
            AuthoritativeItemRegistry.MarkAuthoredIdentity(this);
    }

    internal void AdoptAuthoredItemKey(string key, bool replenishableOfficeSlot = false,
        bool authoredShopSlot = false)
    {
        if (!WorldItemStableIdentity.IsValid(key)) return;
        AuthoredItemKey = key;
        IsReplenishableAuthoredOfficeSlot |= replenishableOfficeSlot;
        IsAuthoredShopSlot |= authoredShopSlot;
        AuthoritativeItemRegistry.MarkAuthoredIdentity(this);
        NetworkedItemManager.Instance?.ReplaceAuthoredItemProjection(key, this);
    }

    internal string DetachAuthoredItemKey()
    {
        string key = AuthoredItemKey ?? string.Empty;
        if (!WorldItemStableIdentity.IsValid(key))
            return string.Empty;
        AuthoredItemKey = string.Empty;
        IsReplenishableAuthoredOfficeSlot = false;
        IsAuthoredShopSlot = false;
        AuthoritativeItemRegistry.PromoteAuthoredToPersistent(this);
        return key;
    }

    /// <summary>
    /// Clears authority and transition state from the previous NetId of an exact scene-authored
    /// projection before the host rebinds that same stable authored object.
    /// </summary>
    internal void ResetClientNetworkLifetime()
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        // A dormant authored projection has no live network identity. Be defensive here because
        // a partial retirement may otherwise leave the old dictionary key pointing at it.
        NetId = 0;
        pendingSnapshots.Clear();
        outboundRevisionPipeline.Reset();
        AuthorityRevision = 0;
        PersistentOwnerPlayerId = 0;
        InventoryClaimPlayerId = 0;
        InventoryClaimSlot = -1;
        InventoryClaimFlags = ItemInventoryClaimFlags.None;
        LastTransitionReason = ItemTransitionReason.Unknown;
        playerBelongsToId = 0;
        playerBelongsTo = null;
        UnboundState = ClientItemUnboundState.None;
        recallPreparationPending = false;
        recallPreparationOperationId = 0;
        hostStateObservationPending = false;
        placementSnapshotPending = false;
        coldStorageRetirement = false;
        coldMaterializationPending = false;
        applyingRemoteSnapshot = false;
        stateDirty = false;
        wasThrown = false;
        wasRemoved = false;
        hasLastSentState = false;
        lastSentProjection = default;
        thrownPosition = default;
        thrownRotation = default;
        throwDirection = default;
        LastDirtyTick = 0;
        if (Item?.InventorySpecs != null)
            Item.InventorySpecs.BelongsToPlayer = false;
        MarkValuesClean();
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
        ItemUpdateData snapshot = CreateUpdateData(ItemUpdateData.ItemUpdateType.FullSync);
        return new ItemAdoptionRequestData
        {
            AdoptionToken = token,
            PrefabName = Item?.InventorySpecs?.ItemPrefabName ?? name,
            PlayerProperty = Item?.InventorySpecs?.BelongsToPlayer == true,
            AuthoredItemKey = AuthoredItemKey ?? string.Empty,
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
        if (applyingRemoteSnapshot || recallPreparationPending || coldStorageRetirement ||
            coldMaterializationPending || placementSnapshotPending)
            return;
        stateDirty = true;
        if (!NetworkLifecycle.Instance.IsHost())
            NetworkedItemManager.Instance?.QueueLocalStateObservation(this, reason);
    }

    internal void ProcessLocalStateObservation(string reason)
    {
        if (coldStorageRetirement || coldMaterializationPending)
            return;
        if (Item == null && !Register())
            return;
        if (placementSnapshotPending)
        {
            stateDirty = false;
            PublishLocalState("item.snapshot-suppressed", GetItemState(), lastState, reason,
                "authoritative-placement-pending");
            return;
        }
        ItemState previousObservedState = lastState;
        ItemState currentState = GetItemState();
        if (recallPreparationPending)
        {
            stateDirty = false;
            PublishLocalState("item.snapshot-suppressed", currentState, previousObservedState,
                reason, "recall-prepare-pending");
            return;
        }
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

        byte localPlayerId = InventoryIntegration.LocalPlayerId;
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

        ItemUpdateData snapshot = GetSnapshot(reserveClientRevision: true);
        if (snapshot == null)
        {
            PublishLocalState("item.snapshot-suppressed", currentState, previousObservedState, reason,
                "no-state-or-tracked-value-change");
            return;
        }

        if (!processingAsHost)
        {
            NetworkLifecycle.Instance.Client?.SendItemUpdatePacket(snapshot);
        }
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
}
