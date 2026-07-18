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
    // Unity presentation changes for world, hand, inventory, attachment, and recall states.

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

        byte localPlayerId = InventoryIntegration.LocalPlayerId;
        if (playerId != localPlayerId)
        {
            try
            {
                bool preserveOwnerClaim = PersistentOwnerPlayerId != 0 &&
                    PersistentOwnerPlayerId == localPlayerId && InventoryClaimSlot >= 0 &&
                    HasRetrievalFlag(InventoryClaimFlags) &&
                    Item?.InventorySpecs?.IsEssential == true;
                // Contains(includeDropped: true) also includes the persistent owner's
                // reserved recall silhouette. That is a claim, not active possession.
                // Treating it as possession caused every foreign snapshot to revoke the
                // owner again and made the local observed state oscillate.
                bool hadLocalPossession = Item?.IsGrabbed() == true ||
                    InventoryIntegration.ContainsActive(gameObject);
                grabHandler?.ForceEndInteraction();
                if (preserveOwnerClaim)
                {
                    // The same Unity object backs the reserved silhouette and the remote
                    // world/hand representation. Convert active local inventory state into
                    // a dropped reserved claim before projecting the foreign holder.
                    EnsureLocalOwnerDroppedClaim(targetState);
                }
                else
                    RemoveLocalInventoryMembership(targetState);
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
        {
            if (snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create))
                Item.InventorySpecs.BelongsToPlayer = snapshot.BelongsToPlayer;
            else if (PersistentOwnerPlayerId != 0)
                Item.InventorySpecs.BelongsToPlayer = true;
        }
        gameObject.SetActive(true);
        SetWorldPresentation();
        grabHandler?.TogglePhysics(true);
        ParentToWorld();
        transform.position = snapshot.ItemPosition + WorldMover.currentMove;
        transform.rotation = snapshot.ItemRotation;
        ApplyWorldParent(snapshot);
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
            // GrabHandlerItem.Throw can detach the object as part of ending the interaction. Restore
            // the authoritative parent afterwards; otherwise a train-local throw is immediately
            // converted back to world space and the spatial stream fights that detach every sample.
            if (snapshot.WorldParentKind == ItemWorldParentKind.World)
                ParentToWorld();
            else
                ApplyWorldParent(snapshot);
        }
        else
        {
            wasThrown = false;
            Multiplayer.LogDebug(() => $"NetworkedItem.HandleDroppedOrThrownState() ItemNetId: {snapshot?.ItemNetId} Dropped. Position: {transform.position}");
        }
    }

    private void ApplyWorldParent(ItemUpdateData snapshot)
    {
        Transform parent = null;
        if (snapshot.WorldParentKind == ItemWorldParentKind.TrainInterior &&
            NetworkedTrainCar.TryGet(snapshot.WorldParentNetId, out TrainCar trainCar))
        {
            parent = trainCar.interior ?? trainCar.transform;
            ItemReparentingBase reparenting = GetComponent<ItemReparentingBase>();
            if (reparenting != null)
                reparenting.ParentItemExternal(parent, trainCar.rb, null);
        }
        else if (snapshot.WorldParentKind == ItemWorldParentKind.StaticParent &&
                 WorldItemStaticParentRegistry.TryResolve(snapshot.WorldParentKey, out parent))
        {
            ItemReparentingBase reparenting = GetComponent<ItemReparentingBase>();
            if (reparenting != null)
                reparenting.ParentItemExternal(parent, null, parent.GetComponent<ItemStaticParent>());
        }
        if (parent == null) return;
        if (transform.parent != parent)
            transform.SetParent(parent, false);
        transform.localPosition = snapshot.ParentLocalPosition;
        transform.localRotation = snapshot.ParentLocalRotation;
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
            grabHandler?.ForceEndInteraction();
            if (ShouldPreserveLocalOwnerClaim())
            {
                InventoryIntegration.PurgeContainerMembership(gameObject);
                EnsureLocalOwnerDroppedClaim(targetState);
            }
            else
                RemoveLocalInventoryMembership(targetState);
        }
        catch (Exception exception)
        {
            Multiplayer.LogWarning($"NetworkedItem.DetachForWorldState() inventory cleanup failed for {NetId}: {exception.Message}");
        }

        try
        {
            StorageTransitionPlan plan = StorageIntegration.MoveTo(Item, StorageMembership.World);
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
            else if (plan.RepairedMultipleMembership)
                DebugRuntime.Publish("storage", "storage.multiple-membership-repaired",
                    NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
                    DebugSeverity.Warning, "Item", NetId.ToString(), new()
                    {
                        ["removed"] = plan.Remove.ToString(),
                        ["target"] = StorageMembership.World.ToString()
                    });
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
        byte localPlayerId = InventoryIntegration.LocalPlayerId;
        return localPlayerId != 0 && PersistentOwnerPlayerId == localPlayerId &&
            InventoryClaimSlot >= 0 && HasRetrievalFlag(InventoryClaimFlags) &&
            Item?.InventorySpecs?.IsEssential == true;
    }

    private void EnsureLocalOwnerDroppedClaim(ItemState targetState)
    {
        InventoryClaimExecution execution = InventoryIntegration.EnsureDroppedClaim(
            gameObject, InventoryClaimSlot);
        InventoryClaimPlan plan = execution.Plan;
        if (!execution.Succeeded)
        {
            PublishClaimInvariant(execution.FailureReason, plan.TargetSlot, targetState);
            return;
        }
        if (plan.Action == InventoryClaimAction.None)
            return;

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
        if (!NetworkLifecycle.Instance.IsHost())
            NetworkedItemManager.Instance?.DetachClientAuthoredProjectionForPossession(this, snapshot);
        Multiplayer.LogDebug(() => $"NetworkedItem.HandleInventoryOrHandState() ItemNetId: {snapshot?.ItemNetId} State: {snapshot?.ItemState}. Player: {snapshot?.PlayerId}, Position: {snapshot?.ItemPosition}");

        byte localPlayerId = InventoryIntegration.LocalPlayerId;
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
            {
                // Retrieval claims reserve an exact silhouette slot. Ordinary inventory
                // possession has no claim metadata, so it must use DV's normal free-slot
                // insertion path instead of trying to restore slot -1.
                if (snapshot.InventoryClaimSlot >= 0)
                    RestoreLocalInventoryClaim(snapshot);
                else if (InventoryIntegration.AddActive(gameObject) < 0)
                    PublishClaimInvariant("local-inventory-placement-failed", -1,
                        ItemState.InInventory);
            }
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

    internal bool TryPrepareLocalRecall(uint operationId, int requestedSlot, out string failureReason)
    {
        failureReason = string.Empty;
        if (recallPreparationPending)
        {
            failureReason = "recall-prepare-already-pending";
            return false;
        }

        recallPreparationPending = true;
        recallPreparationOperationId = operationId;
        InventoryClaimExecution execution;
        applyingRemoteSnapshot = true;
        try
        {
            byte localPlayerId = InventoryIntegration.LocalPlayerId;
            PrepareForStateChange(localPlayerId, ItemState.InInventory);
            playerBelongsTo = null;
            playerBelongsToId = localPlayerId;
            execution = InventoryIntegration.RestoreClaim(gameObject, requestedSlot);
        }
        catch (Exception exception)
        {
            failureReason = $"recall-prepare-exception:{exception.GetType().Name}";
            PublishRecallPreparation(false, operationId, requestedSlot, failureReason);
            return false;
        }
        finally
        {
            applyingRemoteSnapshot = false;
        }

        if (!execution.Succeeded)
        {
            failureReason = execution.FailureReason;
            PublishClaimInvariant(failureReason, execution.Plan.TargetSlot, ItemState.InInventory);
            PublishRecallPreparation(false, operationId, requestedSlot, failureReason);
            return false;
        }

        stateDirty = false;
        hostStateObservationPending = false;
        PublishRecallPreparation(true, operationId, requestedSlot, string.Empty);
        return true;
    }

    internal void CompleteLocalRecallPreparation(uint operationId, bool accepted)
    {
        if (!recallPreparationPending || recallPreparationOperationId != operationId)
            return;
        recallPreparationPending = false;
        recallPreparationOperationId = 0;
        stateDirty = false;
        hostStateObservationPending = false;
        DebugRuntime.Publish("item", accepted ? "item.recall-transaction-committed" :
                "item.recall-transaction-cancelled",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            accepted ? DebugSeverity.Info : DebugSeverity.Warning, "Item", NetId.ToString(), new()
            {
                ["operationId"] = operationId,
                ["authorityRevision"] = AuthorityRevision
            });
    }

    private void PublishRecallPreparation(bool succeeded, uint operationId, int requestedSlot,
        string failureReason)
    {
        DebugRuntime.Publish("item", succeeded ? "item.recall-prepare-succeeded" :
                "item.recall-prepare-failed",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            succeeded ? DebugSeverity.Info : DebugSeverity.Warning, "Item", NetId.ToString(), new()
            {
                ["operationId"] = operationId,
                ["requestedSlot"] = requestedSlot,
                ["authorityRevision"] = AuthorityRevision,
                ["failureReason"] = failureReason ?? string.Empty
            });
    }

    private bool RestoreLocalInventoryClaim(ItemUpdateData snapshot)
    {
        int requestedSlot = snapshot.InventoryClaimSlot;
        InventoryClaimExecution execution = InventoryIntegration.RestoreClaim(gameObject, requestedSlot);
        InventoryClaimPlan plan = execution.Plan;
        if (!execution.Succeeded)
        {
            PublishClaimInvariant(execution.FailureReason, plan.TargetSlot, ItemState.InInventory);
            return false;
        }

        DebugRuntime.Publish("inventory", "inventory.essential-claim-restored",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: NetId.ToString(), data: new()
            {
                ["requestedSlot"] = requestedSlot,
                ["restoredSlot"] = execution.CurrentSlot,
                ["plan"] = plan.Action.ToString(),
                ["reserved"] = execution.Reserved,
                ["dropped"] = execution.Dropped,
                ["persistentOwnerPlayerId"] = PersistentOwnerPlayerId,
                ["authorityRevision"] = AuthorityRevision
            });
        return true;
    }

    internal void ApplyAuthorityMetadata(ItemUpdateData snapshot)
    {
        if (snapshot == null)
            return;
        AuthorityRevision = snapshot.AuthorityRevision;
        if (!NetworkLifecycle.Instance.IsHost())
        {
            byte localPlayerId = InventoryIntegration.LocalPlayerId;
            bool ownAcknowledgement = localPlayerId != 0 &&
                snapshot.OriginatingPlayerId == localPlayerId;
            outboundRevisionPipeline.ObserveCanonical(AuthorityRevision,
                preserveReservations: ownAcknowledgement);
        }
        PersistentOwnerPlayerId = snapshot.PersistentOwnerPlayerId;
        InventoryClaimPlayerId = snapshot.InventoryClaimPlayerId;
        InventoryClaimSlot = snapshot.InventoryClaimSlot;
        InventoryClaimFlags = snapshot.InventoryClaimFlags;
        LastTransitionReason = snapshot.TransitionReason;
        // Persistent ownership is the authoritative multiplayer classification for a personal
        // item. A retrieval claim is a separate capability: ordinary adopted inventory items
        // still belong to their player and must remain eligible for Lost and Found.
        if (PersistentOwnerPlayerId != 0 && Item?.InventorySpecs != null)
            Item.InventorySpecs.BelongsToPlayer = true;
    }

    private void PublishHolderInvariant(ItemUpdateData snapshot)
    {
        byte localPlayerId = InventoryIntegration.LocalPlayerId;
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
        if (snapshot == null)
            return;
        byte localPlayerId = InventoryIntegration.LocalPlayerId;
        if (localPlayerId == 0 || PersistentOwnerPlayerId != 0 && PersistentOwnerPlayerId != localPlayerId)
            return;
        try
        {
            Inventory inventory = Inventory.Instance;
            int slot = inventory?.IndexOf(gameObject) ?? -1;
            // Derail Valley's item-getter button is specifically gated by
            // InventorySlotDisplayData.IsItemGetter, which is copied from IsEssential.
            // Reservation is also used for ordinary inventory bookkeeping and must not
            // grant those items recall silhouettes or foreign-item styling.
            if (slot < 0 || Item?.InventorySpecs?.IsEssential != true)
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

    private void RemoveLocalInventoryMembership(ItemState targetState)
    {
        InventoryRemovalSnapshot removal = InventoryIntegration.RevokeMembership(gameObject);
        if (removal.SlotBefore < 0 && removal.EquippedSlotBefore < 0 && !removal.ContainedBefore)
            return;
        DebugRuntime.Publish("inventory", "item.local-inventory-membership-revoked",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            removal.ContainedAfter || removal.SlotAfter >= 0 || removal.EquippedSlotAfter >= 0
                ? DebugSeverity.Warning : DebugSeverity.Info,
            "Item", NetId.ToString(), new()
            {
                ["targetState"] = targetState.ToString(),
                ["slotBefore"] = removal.SlotBefore,
                ["equipSlotBefore"] = removal.EquippedSlotBefore,
                ["containedBefore"] = removal.ContainedBefore,
                ["slotAfter"] = removal.SlotAfter,
                ["equipSlotAfter"] = removal.EquippedSlotAfter,
                ["containedAfter"] = removal.ContainedAfter
            });
    }


    private static bool HasRetrievalFlag(ItemInventoryClaimFlags flags) =>
        (flags & (ItemInventoryClaimFlags.Reserved | ItemInventoryClaimFlags.Locked)) != 0;
}
