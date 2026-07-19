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

public partial class NetworkedItem
{
    // Tracked values, snapshot composition/receipt, and authority metadata.
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

        bool releasePlacementGate = placementSnapshotPending;
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
        placementSnapshotPending = false;
        if (releasePlacementGate && !NetworkLifecycle.Instance.IsHost())
            SetClientNetworkBinding(true);

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


    public ItemUpdateData GetSnapshot(bool reserveClientRevision = false)
    {
        if (coldStorageRetirement || coldMaterializationPending)
        {
            hostStateObservationPending = false;
            stateDirty = false;
            MarkValuesClean();
            return null;
        }
        ItemUpdateData snapshot;
        ItemUpdateData.ItemUpdateType updateType = ItemUpdateData.ItemUpdateType.None;

        bool hasDirtyVals = HasDirtyValues();

        if (Item == null && Register() == false)
        {
            hostStateObservationPending = false;
            return null;
        }

        if (NetworkLifecycle.Instance.IsHost() && NetId != 0 &&
            AuthoritativeItemRegistry.TryGet(NetId, out AuthoritativeItemRegistry.Record authority) &&
            authority.Placement == ItemPlacementKind.LostAndFound)
        {
            hostStateObservationPending = false;
            createdDirty = false;
            stateDirty = false;
            wasThrown = false;
            wasRemoved = false;
            MarkValuesClean();
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
        uint? authorityRevisionOverride = reserveClientRevision && !NetworkLifecycle.Instance.IsHost()
            ? outboundRevisionPipeline.Reserve(AuthorityRevision)
            : null;
        snapshot = CreateUpdateData(updateType, authorityRevisionOverride);
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

    internal void BeginColdStorageRetirement()
    {
        coldStorageRetirement = true;
        hostStateObservationPending = false;
        stateDirty = false;
        wasThrown = false;
        wasRemoved = false;
        MarkValuesClean();
    }

    internal void BeginColdMaterialization()
    {
        coldMaterializationPending = true;
        hostStateObservationPending = false;
        stateDirty = false;
        wasThrown = false;
        wasRemoved = false;
        MarkValuesClean();
    }

    internal void CompleteColdMaterialization()
    {
        coldMaterializationPending = false;
        coldStorageRetirement = false;
        hostStateObservationPending = false;
        stateDirty = false;
        wasThrown = false;
        wasRemoved = false;
        MarkValuesClean();
    }

    internal void AbortColdMaterialization()
    {
        coldMaterializationPending = false;
        BeginColdStorageRetirement();
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

        if (ItemSnapshotApplicationPolicy.IsLocalThrowAcknowledgement(
                NetworkLifecycle.Instance.IsHost(),
                NetworkLifecycle.Instance.Client?.PlayerId ?? 0,
                snapshot.OriginatingPlayerId,
                ToWireState(snapshot.ItemState)))
        {
            ApplyLocalThrowAcknowledgement(snapshot);
            return;
        }

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
                placementSnapshotPending = true;
                if (!NetworkLifecycle.Instance.IsHost())
                    SetClientNetworkBinding(false);
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

    internal void ApplyClientLostAndFoundProjection(ItemUpdateData snapshot)
    {
        if (snapshot == null || NetworkLifecycle.Instance.IsHost())
            return;

        ApplyAuthorityMetadata(snapshot);
        SetClientNetworkBinding(true);
        EnforceClientLostAndFoundProjection();
        createdDirty = false;
        stateDirty = false;
        hasLastSentState = true;
        EntityDebugRegistry.UpdateState("Item", NetId.ToString(),
            EntityDebugRegistry.ItemState(this));
    }

    private void EnforceClientLostAndFoundProjection()
    {
        byte localPlayerId = InventoryIntegration.LocalPlayerId;
        bool preserveOwnerSilhouette = localPlayerId != 0 &&
            PersistentOwnerPlayerId == localPlayerId &&
            Item?.InventorySpecs?.IsEssential == true && InventoryClaimSlot >= 0 &&
            HasRetrievalFlag(InventoryClaimFlags);
        try
        {
            Inventory inventory = Inventory.Instance;
            int slot = inventory?.IndexOf(gameObject) ?? -1;
            bool membershipSecure = preserveOwnerSilhouette
                ? slot == InventoryClaimSlot && inventory.GetSlotDroppedState(slot) &&
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
                InventoryIntegration.EnsureDroppedClaim(gameObject, InventoryClaimSlot);
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
        stateDirty = false;
    }

    internal void RestoreClientLostAndFoundProjection(ItemUpdateData snapshot)
    {
        if (snapshot == null || NetworkLifecycle.Instance.IsHost())
            return;
        SetClientNetworkBinding(true);
        gameObject.SetActive(true);
        if (Item != null)
            Item.InteractionAllowed = true;
        ReceiveSnapshot(snapshot);
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

    private void ApplyLocalThrowAcknowledgement(ItemUpdateData snapshot)
    {
        lastState = ObservationBaselineAfterSnapshot(snapshot.ItemState);
        lastSentProjection = ItemWireStateComparer.StableBaselineAfterSend(
            ProjectionFromSnapshot(snapshot));
        hasLastSentState = true;
        createdDirty = false;
        stateDirty = false;
        wasThrown = false;
        hostStateObservationPending = false;
        MarkValuesClean();
        EntityDebugRegistry.UpdateState("Item", NetId.ToString(),
            EntityDebugRegistry.ItemState(this));
        DebugRuntime.Publish("item", "item.local-throw-acknowledged",
            DebugRuntimeSide.Client, entityType: "Item", entityId: NetId.ToString(), data: new()
            {
                ["authorityRevision"] = snapshot.AuthorityRevision,
                ["originatingPlayerId"] = snapshot.OriginatingPlayerId,
                ["velocityPreserved"] = DebugValueSnapshotter.Snapshot(
                    Item?.ItemRigidbody?.velocity),
                ["angularVelocityPreserved"] = DebugValueSnapshotter.Snapshot(
                    Item?.ItemRigidbody?.angularVelocity)
            });
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

    public ItemUpdateData CreateUpdateData(ItemUpdateData.ItemUpdateType updateType,
        uint? authorityRevisionOverride = null)
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
        ItemWorldParentKind worldParentKind = ItemWorldParentKind.World;
        ushort worldParentNetId = 0;
        string worldParentKey = string.Empty;
        Vector3 parentLocalPosition = Vector3.zero;
        Quaternion parentLocalRotation = Quaternion.identity;

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
        else if (lastState is ItemState.Dropped or ItemState.Thrown)
        {
            TryGetPhysicalTrainParent(out TrainCar parentCar);
            ItemStaticParent staticParent = GetComponentInParent<ItemStaticParent>();
            if (parentCar != null)
            {
                worldParentKind = ItemWorldParentKind.TrainInterior;
                worldParentNetId = parentCar.GetNetId();
                parentLocalPosition = parentCar.interior != null
                    ? parentCar.interior.InverseTransformPoint(transform.position)
                    : parentCar.transform.InverseTransformPoint(transform.position);
                parentLocalRotation = Quaternion.Inverse((parentCar.interior ?? parentCar.transform).rotation) * transform.rotation;
            }
            else if (staticParent != null)
            {
                worldParentKind = ItemWorldParentKind.StaticParent;
                worldParentKey = WorldItemStableIdentity.CaptureStaticParent(staticParent.transform);
                parentLocalPosition = staticParent.transform.InverseTransformPoint(transform.position);
                parentLocalRotation = Quaternion.Inverse(staticParent.transform.rotation) * transform.rotation;
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
            AuthorityRevision = authorityRevisionOverride ?? AuthorityRevision,
            PersistentOwnerPlayerId = PersistentOwnerPlayerId,
            InventoryClaimPlayerId = InventoryClaimPlayerId,
            InventoryClaimSlot = InventoryClaimSlot,
            InventoryClaimFlags = InventoryClaimFlags,
            TransitionReason = LastTransitionReason,
            OriginatingPlayerId = NetworkLifecycle.Instance.Client?.PlayerId ?? 0,
            AuthoredItemKey = updateType.HasFlag(ItemUpdateData.ItemUpdateType.Create)
                ? AuthoredItemKey ?? string.Empty
                : string.Empty,
            BelongsToPlayer = Item.InventorySpecs.BelongsToPlayer,
            WorldParentKind = worldParentKind,
            WorldParentNetId = worldParentNetId,
            WorldParentKey = worldParentKey,
            ParentLocalPosition = parentLocalPosition,
            ParentLocalRotation = parentLocalRotation
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

        // Derail Valley exposes only this process's local inventory. Preserve another player's
        // accepted placement on both host and clients; otherwise a remote InInventory projection
        // is recomputed as Dropped merely because it is absent from the local Inventory singleton.
        byte localPlayerId = InventoryIntegration.LocalPlayerId;
        if (ItemStateObservationPolicy.PreserveRemotePlayerPlacement(
                playerBelongsToId, localPlayerId, ToWireState(lastState)))
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

        byte localPlayerId = InventoryIntegration.LocalPlayerId;
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
}
