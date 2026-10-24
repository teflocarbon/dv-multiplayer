using DV;
using DV.CabControls;
using DV.InventorySystem;
using DV.Interaction;
using DV.Items;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.Physics;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.Behaviours;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using UnityEngine;

namespace Multiplayer.Client.Items.LiveState;

/// <summary>
/// The remote client's half of one <see cref="NetworkedItem"/> (<see cref="INetworkedItemClientPart"/>):
/// whether it is bound to a host-issued id and so usable, its Lost and Found tombstone, the
/// end of its network lifetime when the host retires it, the release grace
/// and object-state send gate, and sending the local player's changes to the host (with the revision
/// they claim). Made by the item from the local
/// session (<see cref="Session.NetworkClient"/>) on a remote client only; client code reaches it as
/// <c>item.Client()</c>.
/// </summary>
public sealed class NetworkedItemClient : INetworkedItemClientPart
{
    private readonly NetworkedItem item;

    // The item's own state, as this code read it when it was part of the item.
    private ItemBase Item => item.Item;
    private GameObject gameObject => item.gameObject;
    private Transform transform => item.transform;
    private ushort NetId => item.NetId;

    private bool clientBindingGateApplied;
    private bool interactionAllowedBeforeBindingGate;
    private readonly ItemOutboundRevisionPipeline outboundRevisionPipeline = new();

    // The release grace (DefersRelease).
    private float releasedAt = -1f;     // when a local hand last let go, until it's sent or re-grabbed
    private float releaseSpeed = -1f;
    private int releasedFrame;
    private bool releaseDeferralPublished;

    public ClientItemUnboundState UnboundState { get; private set; }

    internal NetworkedItemClient(NetworkedItem item)
    {
        this.item = item;
    }

    public void RegisterIfUnbound()
    {
        if (NetId == 0)
            NetworkedItemManager.Instance?.ClientPart?.RegisterUnboundClientItem(item);
    }

    public void SetNetworkBinding(bool isBound)
    {
        if (isBound && NetId != 0)
        {
            UnboundState = ClientItemUnboundState.None;

            // A non-host item with an authoritative ID represents an object the server
            // has already created.  Clear the local creation edge immediately rather
            // than waiting for its first snapshot to apply: tracked-value setup can
            // defer that snapshot, and LateUpdate must not emit a client Create while
            // the item is waiting in that queue.
            item.CreatedDirty = false;
        }

        GrabHandlerItem grabHandler = item.GrabHandler;
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

    /// <summary>
    /// Clears authority and transition state from the previous NetId of an exact scene-authored
    /// projection before the host rebinds that same stable authored object.
    /// </summary>
    internal void ResetNetworkLifetime()
    {
        // A dormant authored projection has no live network identity. Be defensive here because
        // a partial retirement may otherwise leave the old dictionary key pointing at it.
        outboundRevisionPipeline.Reset();
        UnboundState = ClientItemUnboundState.None;
        releasedAt = -1f;
        releaseSpeed = -1f;
        releaseDeferralPublished = false;
        item.ForgetNetworkLifetime();
    }

    internal void GateAsSceneObjectAwaitingHostCreate()
    {
        UnboundState = ClientItemUnboundState.SceneObjectAwaitingHostCreate;
        SetNetworkBinding(false);
    }

    public void QueueObservation(string reason) =>
        NetworkedItemManager.Instance?.QueueLocalStateObservation(item, reason);

    public void NoteReleased(float releasedAt)
    {
        this.releasedAt = releasedAt;
        releasedFrame = Time.frameCount;
        releaseSpeed = -1f;
    }

    public void NoteGrabbed() => releasedAt = -1f;

    public bool DefersRelease(ItemState currentState)
    {
        if (releasedAt < 0f)
            return false;
        float now = Time.realtimeSinceStartup;
        // VRTK gives a thrown body its velocity as it lets go, not necessarily before the
        // Ungrabbed event: measure from the next frame.
        if (releaseSpeed < 0f && Time.frameCount > releasedFrame)
            releaseSpeed = item.RelativeReleaseSpeed();
        if (ItemReleaseGrace.ShouldDefer(item.LastSentWireState, NetworkedItem.ToWireState(currentState), item.WasThrown,
                Mathf.Max(releaseSpeed, 0f), now - releasedAt))
        {
            if (!releaseDeferralPublished && releaseSpeed >= 0f)
            {
                releaseDeferralPublished = true;
                item.PublishLocalState("item.release-deferred", currentState, item.LastObservedState, "released",
                    $"release-grace speed={releaseSpeed:F2}");
            }
            return true;
        }
        releasedAt = -1f;
        releaseDeferralPublished = false;
        return false;
    }

    public bool MaySendObjectState(ItemState currentState, out bool worldInteraction, out bool pageOnly)
    {
        worldInteraction = false;
        pageOnly = false;
        if (ItemObjectStateAuthority.ClientMaySend(NetworkedItem.ToWireState(currentState), pageOnly: false,
                localInteraction: false))
            return true;
        worldInteraction = item.IsLocalInteractionOpen;
        pageOnly = !worldInteraction && item.IsTrackedValueDirty(NetworkedPageBookState.TrackedValueKey);
        return worldInteraction || pageOnly;
    }

    public void SendLocalObservation(ItemState currentState, ItemState previousObservedState, string reason)
    {
        if (item.IsRemotePlayerAuthoritative())
        {
            item.StateDirty = false;
            item.MarkValuesClean();
            item.PublishLocalState("item.snapshot-suppressed", currentState, previousObservedState, reason,
                "remote-player-authoritative");
            return;
        }

        if (NetId == 0)
        {
            // Not the server's: a client never makes its own items (owned-items.md, PR 5).
            item.StateDirty = false;
            item.PublishLocalState("item.snapshot-suppressed", currentState, previousObservedState, reason,
                "unbound-local-item");
            return;
        }

        ItemUpdate snapshot = item.GetSnapshot(reserveClientRevision: true);
        if (snapshot == null)
        {
            item.PublishLocalState("item.snapshot-suppressed", currentState, previousObservedState, reason,
                "no-state-or-tracked-value-change");
            return;
        }

        // A player's own change is a request (docs/design/items/wire.md): the server answers it
        // with the update everyone gets, or a correction.
        ItemRequest request = ItemRequestTranslation.FromObservation(snapshot);
        if (request == null)
        {
            item.PublishLocalState("item.snapshot-suppressed", currentState, previousObservedState, reason,
                "not-a-request");
            return;
        }
        // The item's own state goes up with it, for its row (OI-COLD-13).
        if (request.Action == ItemAction.Stash)
            request.SavedState = ItemStateText.Capture(gameObject);
        ItemClient.Current?.SendItemRequest(request);
    }

    public uint ReserveRevision() => outboundRevisionPipeline.Reserve(item.AuthorityRevision);

    public void ObserveCanonicalRevision(ItemUpdate snapshot)
    {
        byte localPlayerId = InventoryIntegration.LocalPlayerId;
        bool ownAcknowledgement = localPlayerId != 0 &&
            snapshot.OriginatingPlayerId == localPlayerId;
        outboundRevisionPipeline.ObserveCanonical(item.AuthorityRevision,
            preserveReservations: ownAcknowledgement);
    }

    // Host convergence re-send (ServerItemManager.Convergence): only act when this copy actually
    // disagrees, so the periodic pass never nudges consistent items.
    public bool IgnoresConvergence(ItemUpdate snapshot) =>
        snapshot.TransitionReason == ItemTransitionReason.Convergence && !DisagreesWithConvergence(snapshot);

    private const float ConvergencePositionTolerance = 0.25f;

    private bool DisagreesWithConvergence(ItemUpdate snapshot)
    {
        if (Item == null || Item.IsGrabbed())
            return false; // a local interaction is in flight; the host will hear about it
        if (snapshot.AuthorityRevision > item.AuthorityRevision)
            return true; // a canonical change never reached us
        if (item.GetItemState() is not (ItemState.Dropped or ItemState.Thrown))
            return true; // the host has it lying in the world; we have it somewhere else

        float tolerance2 = ConvergencePositionTolerance * ConvergencePositionTolerance;
        if (snapshot.WorldParentKind == ItemWorldParentKind.TrainInterior)
        {
            // Compare in the car's frame so differences in train motion aren't mistaken for drift.
            if (!NetworkedTrainCar.TryGet(snapshot.WorldParentNetId, out TrainCar car) || car == null)
                return false; // that car isn't here; nothing to compare against
            if (TrainCar.Resolve(transform) != car)
                return true;
            Transform frame = car.interior != null ? car.interior : car.transform;
            return (frame.InverseTransformPoint(transform.position) - snapshot.ParentLocalPosition).sqrMagnitude > tolerance2;
        }
        return (transform.position - WorldMover.currentMove - snapshot.ItemPosition).sqrMagnitude > tolerance2;
    }

    public bool IsLocalDropAcknowledgement(ItemUpdate snapshot) =>
        snapshot.TransitionReason == ItemTransitionReason.ClientState &&
        snapshot.ItemState == ItemState.Dropped &&
        snapshot.OriginatingPlayerId != 0 &&
        snapshot.OriginatingPlayerId == NetworkLifecycle.Instance.LocalSession?.PlayerId &&
        Item != null && !Item.IsGrabbed() &&
        item.GetItemState() == ItemState.Dropped;

    public bool EnforceLostAndFoundTombstone()
    {
        if (NetworkedItemManager.Instance?.ClientPart?.IsLostAndFoundTombstoned(NetId) != true)
            return false;
        EnforceLostAndFoundProjection();
        return true;
    }

    internal void ApplyLostAndFoundProjection(ItemUpdate snapshot)
    {
        if (snapshot == null)
            return;

        item.ApplyAuthorityMetadata(snapshot);
        SetNetworkBinding(true);
        EnforceLostAndFoundProjection();
        item.CreatedDirty = false;
        item.StateDirty = false;
        item.HasLastSentState = true;
        EntityDebugRegistry.UpdateState("Item", NetId.ToString(),
            EntityDebugRegistry.ItemState(item));
    }

    private void EnforceLostAndFoundProjection()
    {
        byte localPlayerId = InventoryIntegration.LocalPlayerId;
        bool preserveOwnerSilhouette = localPlayerId != 0 &&
            item.Relation == ItemRelation.Mine &&
            Item?.InventorySpecs?.IsEssential == true && item.InventoryClaimSlot >= 0 &&
            NetworkedItem.HasRetrievalFlag(item.InventoryClaimFlags);
        try
        {
            DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
            int slot = inventory?.IndexOf(gameObject) ?? -1;
            bool membershipSecure = preserveOwnerSilhouette
                ? slot == item.InventoryClaimSlot && inventory.GetSlotDroppedState(slot) &&
                    inventory.GetSlotReservedState(slot)
                : inventory?.Contains(gameObject, true) != true;
            if (!gameObject.activeSelf && Item?.InteractionAllowed == false &&
                Item?.IsGrabbed() != true && membershipSecure)
                return;
        }
        catch { }

        try { Item?.ForceEndInteraction(); } catch { }
        try
        {
            if (preserveOwnerSilhouette)
                InventoryIntegration.EnsureDroppedClaim(gameObject, item.InventoryClaimSlot);
            else
            {
                InventoryIntegration.RevokeMembership(gameObject, NetId);
                InventoryIntegration.PurgeForeignDroppedClaims("lost-and-found-tombstone");
            }
        }
        catch { }
        try
        {
            StorageController storage = StorageController.Instance;
            if (storage?.StorageWorld?.ContainsItem(Item) == true)
                storage.RemoveItemFromWorldStorage(Item);
        }
        catch { }

        Rigidbody body = Item?.ItemRigidbody;
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
        }
        if (Item != null)
            Item.InteractionAllowed = false;
        gameObject.SetActive(false);
        item.StateDirty = false;
    }

    internal void RestoreLostAndFoundProjection(ItemUpdate snapshot)
    {
        if (snapshot == null)
            return;
        SetNetworkBinding(true);
        gameObject.SetActive(true);
        if (Item != null)
            Item.InteractionAllowed = true;
        item.ReceiveSnapshot(snapshot);
    }
}

internal static class NetworkedItemClientExtensions
{
    /// <summary>The remote client's half of the item; null on the host.</summary>
    internal static NetworkedItemClient Client(this NetworkedItem item) => item?.ClientPart as NetworkedItemClient;
}
