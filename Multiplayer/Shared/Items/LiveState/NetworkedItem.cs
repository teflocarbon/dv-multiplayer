using DV;
using DV.CabControls;
using DV.Customization.Gadgets;
using DV.Interaction;
using DV.InventorySystem;
using DV.Items;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.Physics;
using Multiplayer.Core.Items.Projection;
using Multiplayer.Core.Items.WorldItems;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.Behaviours;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Players;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Collections;
using Multiplayer.Core.Diagnostics;

namespace Multiplayer.Shared.Items.LiveState;

public enum ItemState : byte
{
    Dropped,        //belongs to the world
    Thrown,         //was thrown by player
    InHand,         //held by player
    InInventory,    //in player's inventory
    Attached,       //attached to another object (e.g. EOT Lanterns)
    Installed,      //installed on a car as a gadget: the item is inactive and its projection is on the car
    InMagazine,     //ammo in a tool's magazine (a cassette in a boombox): inactive, the tool shows it
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
        bool b = Get(netId, out IdMonoBehaviour<ushort, NetworkedItem> rawObj);
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

    /// <summary>Host: the player the server last put the item with (in a hand or inventory), 0 for nobody.</summary>
    public byte HostPossessorPlayerId { get; private set; }
    internal void SetHostPossessor(byte playerId) => HostPossessorPlayerId = playerId;

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
    private readonly PendingSnapshotQueue<ItemUpdate> pendingSnapshots = new(
        MaxPendingSnapshots,
        snapshot => snapshot?.AuthorityRevision ?? 0,
        snapshot => snapshot?.UpdateType == ItemUpdate.ItemUpdateType.FullSync);

    //Track dirty states
    private bool createdDirty = true;   //if set, we created this item dirty and have not sent an update
    private ItemState lastState;
    private ItemWireStateProjection lastSentProjection;
    private bool hasLastSentState;
    private bool stateDirty;
    private bool wasThrown;
    // The frame the local VR player let go of a belt-snappable item, or -1 (BeltSlots).
    private int vrBeltReleaseFrame = -1;
    private ItemInteractionWindow localInteraction;

    private Vector3 thrownPosition;
    private Quaternion thrownRotation;
    private Vector3 throwDirection;

    private bool applyingRemoteSnapshot;
    private bool placementSnapshotPending;
    private bool coldStorageRetirement;
    private bool coldMaterializationPending;

    private INetworkedItemClientPart clientPart;

    /// <summary>
    ///     The remote client's half of the item (Client/Items/LiveState/NetworkedItemClient); null on the host.
    ///     Made the first time it's asked for; the host's <c>LocalPlayerSession</c> makes none.
    /// </summary>
    internal INetworkedItemClientPart ClientPart =>
        clientPart ??= ItemClient.Current?.CreateItemPart(this);

    internal ClientItemUnboundState UnboundState => ClientPart?.UnboundState ?? ClientItemUnboundState.None;

    private INetworkedItemServerPart serverPart;

    /// <summary>
    ///     The server's half of the item (Server/Items/LiveState/NetworkedItemServerPart); null on a client.
    ///     Made the first time it's asked for on the server.
    /// </summary>
    internal INetworkedItemServerPart ServerPart =>
        serverPart ??= ItemServer.Current?.CreateItemPart(this);

    // The item's own state, for its client part.
    internal bool CreatedDirty { get => createdDirty; set => createdDirty = value; }
    internal bool StateDirty { get => stateDirty; set => stateDirty = value; }
    internal bool HasLastSentState { get => hasLastSentState; set => hasLastSentState = value; }
    internal ItemState LastObservedState { get => lastState; set => lastState = value; }
    internal bool WasThrown => wasThrown;
    internal WireItemState LastSentWireState => hasLastSentState ? lastSentProjection.State : WireItemState.Dropped;

    internal GrabHandlerItem GrabHandler
    {
        get
        {
            if (grabHandler == null)
                TryGetComponent(out grabHandler);
            return grabHandler;
        }
    }

    public byte playerBelongsToId; // 0 means no owner
    internal ItemState DebugCurrentState => GetItemState();
    internal ItemState DebugLastState => lastState;
    public NetworkedPlayer playerBelongsTo;
    public uint AuthorityRevision { get; private set; }
    /// <summary>
    /// This game's player's relation to the item (D3): the server says only whether it's theirs,
    /// someone else's or nobody's, never who owns it. On the server's own copy, the host's
    /// player's (none on a dedicated host, where every owned item is someone else's).
    /// </summary>
    public ItemRelation Relation { get; private set; }
    public int InventoryClaimSlot { get; private set; } = -1;
    public ItemInventoryClaimFlags InventoryClaimFlags { get; private set; }
    public ItemTransitionReason LastTransitionReason { get; private set; }
    /// <summary>Client: why the host last said this item moved or changed hands (ItemChangeLog).</summary>
    public ChangeCause<ItemMoveReason>? LastMoveCause { get; private set; }
    /// <summary>Client: why the host last said this item went away; it may since have come back.</summary>
    public ChangeCause<ItemRemovalReason>? LastRemovalCause { get; private set; }
    // Host: why the code destroying this item is doing it, for OnDestroy to log and send.
    private ChangeCause<ItemRemovalReason>? pendingRemovalCause;
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
            IsReplenishableAuthoredOfficeSlot =
                AuthoredHierarchyPolicy.ContainsNode(authoredPath, "Offices");
            IsAuthoredShopSlot = GetComponentsInParent<MonoBehaviour>(true).Any(component =>
                component != null && (component.GetType().Namespace ?? string.Empty)
                    .StartsWith("DV.Shops", StringComparison.Ordinal));
        }
        base.Awake();

        // Ensure the NetworkedItemManager is initialised (Register reaches it too, on every game).
        NetworkedItemManager.Instance?.CheckInstance();

        Register();
    }

    protected void Start()
    {
        if (!initialised)
            Register();

        NetworkedItemManager.Instance?.ScheduleTrackedValueFinalization(this);

        ClientPart?.RegisterIfUnbound();

        EntityDebugRegistry.RegisterItem(this);
        DebugRuntime.Publish("item", "item.registered", NetworkRole.DebugSide,
            entityType: "Item", entityId: NetId.ToString(), data: EntityDebugRegistry.ItemState(this));
    }

    protected void OnEnable()
    {
        NetworkedItemManager.Instance?.ScheduleTrackedValueFinalization(this);
        ClientPart?.RegisterIfUnbound();
    }

    protected void LateUpdate()
    {
        if (coldMaterializationPending)
            return;
        if (ClientPart?.EnforceLostAndFoundTombstone() == true)
            return;
        // The server's own change waits for its item manager's dirty pass.
        if (serverPart?.ObservationPending == true)
            return;
        ItemState currentState = GetItemState();
        // An item the server hasn't given an id has nothing to report until it changes. Only a
        // client has those: the server ids every item as it wakes.
        if (NetId == 0 && !stateDirty)
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
            NetworkedItemManager.Instance?.OnTrainItemRemoved(NetId);

        if (!suppressNetworkDestroy)
            DebugRuntime.Publish("item", "item.destroyed", NetworkRole.DebugSide,
                entityType: "Item", entityId: NetId.ToString(), data: EntityDebugRegistry.ItemState(this));
        EntityDebugRegistry.Unregister("Item", NetId.ToString());
        DebugDesyncDetector.Forget(NetId);

        // The host logs why it went, keeps a tombstone, sends the Destroy and drops its record.
        ItemServer.Current?.OnItemDestroyed(this, suppressNetworkDestroy);

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

        base.OnDestroy();
    }

    /// <summary>
    /// Host: says why the item is about to be destroyed, so OnDestroy logs and sends that instead
    /// of <see cref="ItemRemovalReason.DestroyedByGame"/>. Call it before Object.Destroy.
    /// </summary>
    internal void NoteRemovalCause(ChangeCause<ItemRemovalReason> cause) => pendingRemovalCause = cause;

    /// <summary>
    /// Server: the item goes on as data only (a remote player's inventory item, OI-COLD-06) and this
    /// object goes. Its NetId stays its own; the object made for it next takes it up.
    /// </summary>
    internal void HandOverToData()
    {
        BeginColdStorageRetirement();
        HandOverNetId();
    }

    /// <summary>Server: this new object stands for an item kept as data, under its NetId.</summary>
    internal bool TakeUpFromData(ushort netId) => TakeUpNetId(netId);

    /// <summary>Server: a NetId for an item kept as data from the start (a joiner's inventory row), or 0.</summary>
    internal static ushort ReserveDataNetId() => ReserveNetId(out ushort netId) ? netId : (ushort)0;

    /// <summary>Server: an item kept as data is gone for good; its NetId is free.</summary>
    internal static void FreeDataNetId(ushort netId) => FreeHandedOverNetId(netId);
    internal ChangeCause<ItemRemovalReason>? PendingRemovalCause => pendingRemovalCause;
    internal bool ColdStorageRetirement => coldStorageRetirement;
    internal bool ColdMaterializationPending => coldMaterializationPending;

    internal void SetLastMoveCause(ChangeCause<ItemMoveReason> cause) => LastMoveCause = cause;
    internal void SetLastRemovalCause(ChangeCause<ItemRemovalReason> cause) => LastRemovalCause = cause;

    /// <summary>
    /// When the local player last let go of this item (realtime), or -1 if they hold it or never
    /// did. Unlike a client's release grace (NetworkedItemClient.DefersRelease), it isn't cleared
    /// once the drop is sent.
    /// </summary>
    internal float LocallyReleasedAt { get; private set; } = -1f;

    private void OnUngrabbed(ControlImplBase obj)
    {
        if (VRManager.IsVREnabled() && Item != null && Item.IsBeltSnappable)
            vrBeltReleaseFrame = Time.frameCount;
        RefreshLocalVrHandAfterInteraction();
        LocallyReleasedAt = Time.realtimeSinceStartup;
        ClientPart?.NoteReleased(LocallyReleasedAt);
        MarkLocalStateDirty("released");
    }

    private void OnGrabbed(ControlImplBase obj)
    {
        playerBelongsToId = NetworkRole.LocalPlayerId;
        RefreshLocalVrHandAfterInteraction();
        ClientPart?.NoteGrabbed();
        LocallyReleasedAt = -1f;
        MarkLocalStateDirty("grabbed");
    }

    // Measured against the car the player is on: a gentle drop in a moving cab isn't a throw.
    // A body DV has already parented to a car moves relative to it.
    internal float RelativeReleaseSpeed()
    {
        Rigidbody body = Item?.ItemRigidbody;
        if (body == null || body.isKinematic)
            return 0f;
        Vector3 velocity = body.velocity;
        Rigidbody car = PlayerManager.Car?.rb;
        if (car != null && !TryGetPhysicalTrainParent(out _))
            velocity -= car.velocity;
        return velocity.magnitude;
    }

    // The local player working this item without holding it (a set-down lantern's knob, lighting
    // it): only then may its game send this item's object state (ItemObjectStateAuthority).
    internal void BeginLocalInteraction()
    {
        localInteraction.Begin();
        MarkLocalStateDirty("interaction-began");
    }

    internal void EndLocalInteraction()
    {
        localInteraction.End(Time.realtimeSinceStartup);
        MarkLocalStateDirty("interaction-ended");
    }

    internal void TouchLocalInteraction()
    {
        localInteraction.Touch(Time.realtimeSinceStartup);
        MarkLocalStateDirty("interaction-used");
    }

    internal bool IsLocalInteractionOpen => localInteraction.IsOpen(Time.realtimeSinceStartup);

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

    public T GetTrackedItem<T>() where T : Component
    {
        return UsefulItem ? trackedItem as T : null;
    }

    public void Initialize<T>(T item, ushort netId = 0, bool createDirty = true) where T : Component
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.Initialize<{typeof(T)}>(netId: {netId}, name: {name}, createDirty: {createdDirty})");

        if (netId != 0)
        {
            NetworkedItemManager.Instance?.ClientPart?.ResolveAuthoritativeBindingCollision(this, netId, typeof(T));
            NetId = netId;
            ClientPart?.SetNetworkBinding(true);
        }

        trackedItem = item;
        TrackedItemType = typeof(T);
        UsefulItem = true;

        createdDirty = createDirty;

        if (Item == null)
            Register();

        NetworkedItemManager.Instance?.ScheduleTrackedValueFinalization(this);
        NetworkedItemManager.Instance?.ClientPart?.ApplyPendingSpecialSnapshot(this);

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

            ClientPart?.RegisterIfUnbound();

            lastState = GetItemState();
            stateDirty = false;

            initialised = true;
            NetworkedItemManager.Instance?.ScheduleTrackedValueFinalization(this);
            NetworkedItemManager.Instance?.RegisterHostWorldItem(this);
            return true;
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"NetworkedItem.Register() Unable to find ItemBase for {name}\r\n{ex.Message}");
            return false;
        }
    }

    internal void EnsureAuthoredItemKey()
    {
        // Never promote an after-load dynamic materialization into the authored catalogue.
        // The only valid source is the content-scene staging hierarchy observed during Awake.
        if (!WorldItemStableIdentity.IsValid(AuthoredItemKey) && Item != null &&
            WorldItemStableIdentity.IsLikelyAuthoredAtAwake(Item))
            AuthoredItemKey = WorldItemStableIdentity.Capture(Item);
        if (IsSceneAuthored)
            ItemServer.Current?.MarkAuthoredIdentity(this);
    }

    internal void AdoptAuthoredItemKey(string key, bool replenishableOfficeSlot = false,
        bool authoredShopSlot = false)
    {
        if (!WorldItemStableIdentity.IsValid(key)) return;
        AuthoredItemKey = key;
        IsReplenishableAuthoredOfficeSlot |= replenishableOfficeSlot;
        IsAuthoredShopSlot |= authoredShopSlot;
        ItemServer.Current?.MarkAuthoredIdentity(this);
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
        ItemServer.Current?.PromoteAuthoredToPersistent(this);
        return key;
    }

    /// <summary>
    /// The item's half of its client part's reset (NetworkedItemClient.ResetNetworkLifetime): forgets
    /// the authority and transition state of its previous NetId.
    /// </summary>
    internal void ForgetNetworkLifetime()
    {
        NetId = 0;
        pendingSnapshots.Clear();
        AuthorityRevision = 0;
        Relation = ItemRelation.Nobodys;
        InventoryClaimSlot = -1;
        InventoryClaimFlags = ItemInventoryClaimFlags.None;
        LastTransitionReason = ItemTransitionReason.Unknown;
        playerBelongsToId = 0;
        playerBelongsTo = null;
        placementSnapshotPending = false;
        coldStorageRetirement = false;
        coldMaterializationPending = false;
        applyingRemoteSnapshot = false;
        stateDirty = false;
        wasThrown = false;
        localInteraction.Reset();
        hasLastSentState = false;
        ResetVrHandState();
        lastSentProjection = default;
        thrownPosition = default;
        thrownRotation = default;
        throwDirection = default;
        LastDirtyTick = 0;
        if (Item?.InventorySpecs != null)
            Item.InventorySpecs.BelongsToPlayer = false;
        MarkValuesClean();
    }
    private void MarkLocalStateDirty(string reason)
    {
        // ForceEndInteraction, inventory purges and GrabHandlerItem.Throw raise the same base-game
        // callbacks as a local interaction. While projecting an authoritative snapshot those
        // callbacks are side effects, not new player intent, and must not enqueue an echo packet.
        if (applyingRemoteSnapshot || coldStorageRetirement ||
            coldMaterializationPending || placementSnapshotPending)
            return;
        stateDirty = true;
        // A client looks at it in the item manager's next pass; the server at its LateUpdate.
        ClientPart?.QueueObservation(reason);
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
        // A VR release the belt may still take at the end of this frame: send nothing until the
        // next frame says where the item went (BeltSlots.HoldVrRelease). Stays dirty.
        if (HoldingVrRelease())
            return;
        ItemState previousObservedState = lastState;
        ItemState currentState = GetItemState();
        // An installed gadget's placement is the gadget server's to commit (gadgets.md, "The
        // installed gadget and its item"): no game reports it as its own observation.
        if (currentState == ItemState.Installed)
        {
            stateDirty = false;
            lastState = currentState;
            PublishLocalState("item.snapshot-suppressed", currentState, previousObservedState,
                reason, "installed-gadget");
            return;
        }
        // A client's release grace. Stays dirty, so LateUpdate looks again each frame until it's
        // sent or re-grabbed.
        if (NetId != 0 && ClientPart?.DefersRelease(currentState) == true)
            return;
        bool networkStateChanged = HasNetworkStateProjectionChanged(currentState);
        if (currentState != previousObservedState || networkStateChanged)
            stateDirty = true;
        PublishLocalState("item.local-state-observed", currentState, previousObservedState, reason, string.Empty);
        lastState = currentState;

        PublishLocalState(networkStateChanged ? "item.local-state-changed" : "item.local-state-unchanged",
            currentState, previousObservedState, reason, string.Empty);

        // The host's player's change is the server's world already: leave the dirty state for the
        // server item manager's ProcessChanged, which sends it to the other players.
        if (ServerPart is { } server)
        {
            server.NoteLocalObservation(reason);
            return;
        }

        // A remote client sends the change to the host, or asks it to adopt an item it doesn't know.
        ClientPart?.SendLocalObservation(currentState, previousObservedState, reason);
    }

    internal void PublishLocalState(string eventName, ItemState currentState, ItemState previousObservedState,
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
            NetworkRole.DebugSide,
            entityType: "Item", entityId: NetId.ToString(), data: data);
    }

    internal Dictionary<string, object> LocalStateDebugData()
    {
        int inventorySlot = -1;
        string inventoryContainer = string.Empty;
        try
        {
            var container = DV.InventorySystem.Inventory.Instance?.ItemContainerRegistry?.GetItemContainerAndIndex(gameObject);
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
            ["relation"] = Relation.ToString(),
            ["placementPlayerId"] = playerBelongsToId,
            ["hostPossessorPlayerId"] = HostPossessorPlayerId,
            ["inventoryClaimSlot"] = InventoryClaimSlot,
            ["inventoryClaimFlags"] = InventoryClaimFlags.ToString(),
            ["transitionReason"] = LastTransitionReason.ToString(),
            ["lastMoveCause"] = LastMoveCause?.ToString() ?? string.Empty,
            ["lastRemovalCause"] = LastRemovalCause?.ToString() ?? string.Empty
        };
    }
}
