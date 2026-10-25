using DV;
using DV.CabControls;
using DV.Customization.Gadgets;
using DV.Interaction;
using DV.InventorySystem;
using DV.Items;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Inventory;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.Projection;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.Behaviours;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Players;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Collections;

namespace Multiplayer.Shared.Items.LiveState;

public partial class NetworkedItem
{
    // Unity presentation changes for world, hand, inventory, attachment, and recall states.

    /// <summary>Off the snap point it is on, if any (a coupler's lantern, an item on a hanger), into the
    /// world's parent: the server put it somewhere else (Lost and Found, off a deleted car). An unsnap
    /// gives it back the parent it had, which may be the car.</summary>
    internal void LeaveSnapPoint()
    {
        var point = Item?.SnappableItem?.SnappedTo;
        if (point == null)
            return;
        point.UnsnapItem(true);
        transform.SetParent(WorldMover.OriginShiftParent, true);
    }

    private void PrepareForStateChange(byte playerId, ItemState targetState)
    {
        LeaveMagazine(targetState);

        // Cleanup from previous state/desyncs
        if (Item.IsSnapped && !KeepsLocalBeltSnap(playerId, targetState))
            Item.SnappableItem.SnappedTo.UnsnapItem(false); //Todo: should this be forced?

        if (playerBelongsTo != null && playerBelongsTo.PlayerId != playerId)
        {
            playerBelongsTo.DropItem(gameObject);
        }

        // Recover from stale ownership bookkeeping by checking the actual remote hand objects.
        if (playerId == 0 && NetworkLifecycle.Instance.Avatars != null)
            foreach (NetworkedPlayer player in NetworkLifecycle.Instance.Avatars.Players)
                player?.DropItem(gameObject);

        // find new player reference
        playerBelongsToId = playerId;
        playerBelongsTo = null;

        byte localPlayerId = InventoryIntegration.LocalPlayerId;
        if (playerId != localPlayerId)
        {
            try
            {
                bool preserveOwnerClaim = Relation == ItemRelation.Mine && InventoryClaimSlot >= 0 &&
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
                        NetworkRole.DebugSide,
                        entityType: "Item", entityId: NetId.ToString(), data: new()
                        {
                            ["localPlayerId"] = localPlayerId,
                            ["newPlacementPlayerId"] = playerId,
                            ["relation"] = Relation.ToString(),
                            ["authorityRevision"] = AuthorityRevision
                        });
                }
            }
            catch (Exception exception)
            {
                Multiplayer.LogWarning($"Unable to revoke local possession for item {NetId}: {exception.Message}");
            }
        }
        if (playerId != 0 && playerId != localPlayerId && NetworkLifecycle.Instance.Avatars != null)
            if (!NetworkLifecycle.Instance.Avatars.TryGetPlayer(playerId, out playerBelongsTo))
            {
                DebugDiagnostics.ReportDependency("Item", NetId.ToString(), "missing-player", $"playerId={playerId}");
                Multiplayer.LogDebug(() => $"Unable to find player {playerId} for item {NetId}");
            }
            else DebugDiagnostics.ResolveDependency("Item", NetId.ToString(), "missing-player");
    }

    // An item on the local VR player's belt is in their inventory: the snap only shows the slot.
    private bool KeepsLocalBeltSnap(byte playerId, ItemState targetState) =>
        BeltSlots.KeepBeltSnap(ToWireState(targetState), playerId, InventoryIntegration.LocalPlayerId,
            Item.SnappableItem?.SnappedTo is DV.Items.Snapping.ItemSnapPointBelt,
            InventoryIntegration.ContainsActive(gameObject));

    private void HandleDroppedOrThrownState(ItemUpdate snapshot)
    {
        Multiplayer.LogDebug(() => $"NetworkedItem.HandleDroppedOrThrownState([netId: {snapshot?.ItemNetId}, ItemUpdateType: {snapshot?.UpdateType}, ItemState: {snapshot?.ItemState}, PlayerId: {snapshot?.PlayerId}, Active state: {gameObject.activeInHierarchy}])");

        DetachForWorldState(snapshot.ItemState);

        // Inventory-created canonical objects can retain their inactive presentation, inventory
        // layer and kinematic physics after detachment. Normalize the whole object before applying
        // the authoritative world transform; do not route through the host player's drop position.
        if (Item?.InventorySpecs != null)
        {
            if (snapshot.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create))
                Item.InventorySpecs.BelongsToPlayer = snapshot.BelongsToPlayer;
            else if (Relation.IsOwned())
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
        // Settled in a car when this game gets it: it stays kinematic with the car (OI-PHY-06). A
        // live body here was thrown about by the car's motion, and differed from every other game.
        if (snapshot.ItemState == ItemState.Dropped && snapshot.WorldParentKind == ItemWorldParentKind.TrainInterior &&
            (snapshot.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create) ||
             snapshot.TransitionReason == ItemTransitionReason.Convergence))
            NetworkedItemManager.Instance?.Spatial?.HoldSettledTrainItem(this, snapshot);

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

    private void ApplyWorldParent(ItemUpdate snapshot)
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
                 WorldItemStaticParentRegistry.TryResolve(snapshot.WorldParentKey, snapshot.ItemPosition,
                     snapshot.ParentLocalPosition, out parent))
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

    private Dictionary<string, object> DebugApplyState(ItemUpdate snapshot)
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
        if (state == ItemState.InMagazine)
        {
            (ushort tool, byte slot) = MagazineProjection(state);
            return new ItemWireStateProjection(WireItemState.InMagazine, 0, tool, false, slot);
        }
        return new ItemWireStateProjection(ToWireState(state), playerBelongsToId,
            carNetId, attachedFront, (byte)HandForState(state));
    }

    private static ItemWireStateProjection ProjectionFromSnapshot(ItemUpdate snapshot) =>
        snapshot.ItemState == ItemState.InMagazine
            ? new(WireItemState.InMagazine, 0, snapshot.MagazineNetId, false, (byte)Mathf.Clamp(snapshot.MagazineSlot, 0, byte.MaxValue))
            : new(ToWireState(snapshot.ItemState), snapshot.PlayerId, snapshot.CarNetId,
                snapshot.AttachedFront, (byte)snapshot.PlayerHand);

    internal static WireItemState ToWireState(ItemState state) => state switch
    {
        ItemState.Dropped => WireItemState.Dropped,
        ItemState.Thrown => WireItemState.Thrown,
        ItemState.InInventory => WireItemState.InInventory,
        ItemState.InHand => WireItemState.InHand,
        ItemState.Attached => WireItemState.Attached,
        ItemState.Installed => WireItemState.Installed,
        ItemState.InMagazine => WireItemState.InMagazine,
        _ => WireItemState.Dropped
    };

    private void DetachForWorldState(ItemState targetState)
    {
        PublishUnityState("item.detach.before", new() { ["targetState"] = targetState.ToString() });
        PublishUnityState("item.hand-membership.before", new() { ["targetState"] = targetState.ToString() });
        string parentBefore = ParentPath();

        if (playerBelongsTo != null)
            playerBelongsTo.DropItem(gameObject);
        if (NetworkLifecycle.Instance.Avatars != null)
            foreach (NetworkedPlayer player in NetworkLifecycle.Instance.Avatars.Players)
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
                    NetworkRole.DebugSide,
                    DebugSeverity.Warning, "Item", NetId.ToString(), new()
                    {
                        ["reason"] = plan.Reason,
                        ["target"] = StorageMembership.World.ToString()
                    });
            }
            else if (plan.RepairedMultipleMembership)
                DebugRuntime.Publish("storage", "storage.multiple-membership-repaired",
                    NetworkRole.DebugSide,
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
        return localPlayerId != 0 && Relation == ItemRelation.Mine &&
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
            NetworkRole.DebugSide,
            entityType: "Item", entityId: NetId.ToString(), data: new()
            {
                ["slot"] = InventoryClaimSlot,
                ["targetState"] = targetState.ToString(),
                ["relation"] = Relation.ToString()
            });
    }

    private void PublishClaimInvariant(string code, int actualSlot, ItemState targetState)
    {
        DebugRuntime.Publish("inventory", "inventory.essential-claim-invariant-violation",
            NetworkRole.DebugSide,
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
                NetworkRole.DebugSide,
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
        // While another player holds the simulation lease our copy is meant to be kinematic.
        if (Item?.ItemRigidbody?.isKinematic == true &&
            NetworkedItemManager.Instance?.IsSpatiallySimulatedRemotely(NetId) != true)
            violations.Add("rigidbody-kinematic");
        if (transform.parent != WorldMover.OriginShiftParent)
            violations.Add("wrong-world-parent");

        try
        {
            DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
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
            NetworkRole.DebugSide,
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

    internal void PublishUnityState(string eventName, Dictionary<string, object> additional)
    {
        if (!DebugRuntime.EnabledFor("item"))
            return;
        Dictionary<string, object> data = DebugUnityState();
        if (additional != null)
            foreach (KeyValuePair<string, object> value in additional)
                data[value.Key] = value.Value;
        DebugRuntime.Publish("item", eventName, NetworkRole.DebugSide,
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
            ["remoteHandMember"] = playerBelongsTo?.HoldsItem(gameObject) == true,
            ["placementPlayerId"] = playerBelongsToId,
            ["hostPossessorPlayerId"] = HostPossessorPlayerId,
            ["authorityRevision"] = AuthorityRevision,
            ["relation"] = Relation.ToString(),
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
            NetworkedPlayer actualRemoteHolder = NetworkLifecycle.Instance.Avatars != null
                ? NetworkLifecycle.Instance.Avatars.Players.FirstOrDefault(player => player?.HoldsItem(gameObject) == true)
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
        // The parent a reparenting remembers can be a car deleted since; ?. lets a destroyed one through.
        Transform reparentingParent = reparenting != null ? reparenting.CurrentParent : null;
        state["reparentingCurrentParent"] = reparentingParent != null ? reparentingParent.gameObject.GetPath() : string.Empty;

        try
        {
            DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
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

    /// <summary>
    /// An installed gadget's item is inactive: what players see is its projection on the car,
    /// which the gadget code places (gadgets.md, "The installed gadget and its item"). The item
    /// only leaves any hand and goes out of sight; one this game installed itself stays as vanilla
    /// left it, in its installed storage.
    /// </summary>
    private void HandleInstalledState()
    {
        if (StorageController.Instance?.StorageInstalledGadgets?.ContainsItem(Item) == true)
            return;
        DetachForWorldState(ItemState.Installed);
        gameObject.SetActive(false);
    }

    private void HandleAttachedState(ItemUpdate snapshot)
    {
        //handle attaching the item
        gameObject.SetActive(true);
        Multiplayer.LogDebug(() => $"NetworkedItem.HandleAttachedState() ItemNetId: {snapshot?.ItemNetId} attempting attachment to car {snapshot.CarNetId}, at the front {snapshot.AttachedFront}");
        if (snapshot.WorldParentKind == ItemWorldParentKind.SnapPoint)
        {
            SnapToGadget(snapshot);
            return;
        }

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

    // Onto a gadget's snap point (a hanger, a mount), named by its gadget item (live-state.md,
    // "Attached and snapped items"). One its gadget isn't installed for here waits as the coupler's does.
    private void SnapToGadget(ItemUpdate snapshot)
    {
        DV.Items.Snapping.ItemSnapPointBase point = GadgetSnapPoints.PointOf(snapshot.WorldParentNetId);
        if (point == null)
        {
            DebugDiagnostics.ReportDependency("Item", NetId.ToString(), "missing-snap-point", $"gadgetNetId={snapshot.WorldParentNetId}");
            Multiplayer.LogWarning($"NetworkedItem.SnapToGadget() ItemNetId: {snapshot.ItemNetId}. No snap point on gadget item {snapshot.WorldParentNetId}");
            return;
        }
        DebugDiagnostics.ResolveDependency("Item", NetId.ToString(), "missing-snap-point");
        if (snappableItem?.SnappedTo == point)
            return;
        Item.ItemRigidbody.isKinematic = false;
        if (!point.SnapItem(Item, false))
        {
            DebugDiagnostics.ReportDependency("Item", NetId.ToString(), "attachment-failed", $"gadgetNetId={snapshot.WorldParentNetId}");
            Multiplayer.LogWarning($"NetworkedItem.SnapToGadget() Snapping item {snapshot.ItemNetId} to gadget item {snapshot.WorldParentNetId} failed");
        }
        else DebugDiagnostics.ResolveDependency("Item", NetId.ToString(), "attachment-failed");
    }

    private void HandleInventoryOrHandState(ItemUpdate snapshot)
    {
        NetworkedItemManager.Instance?.ClientPart?.DetachAuthoredProjectionForPossession(this, snapshot);
        Multiplayer.LogDebug(() => $"NetworkedItem.HandleInventoryOrHandState() ItemNetId: {snapshot?.ItemNetId} State: {snapshot?.ItemState}. Player: {snapshot?.PlayerId}, Position: {snapshot?.ItemPosition}");

        byte localPlayerId = InventoryIntegration.LocalPlayerId;
        RemoteItemProjectionPlan plan = RemoteItemProjectionPlanner.Plan(
            ToWireState(snapshot.ItemState), snapshot.PlayerId, localPlayerId, playerBelongsTo != null);
        if (plan.ClearExistingRemoteHands && NetworkLifecycle.Instance.Avatars != null)
            foreach (NetworkedPlayer player in NetworkLifecycle.Instance.Avatars.Players)
                player?.DropItem(gameObject);

        if (plan.Action is RemoteItemProjectionAction.LocalHand or
            RemoteItemProjectionAction.LocalInventory)
        {
            playerBelongsTo = null;
            playerBelongsToId = localPlayerId;

            if (plan.Action == RemoteItemProjectionAction.LocalInventory)
            {
                // A VR release that ended on the belt is settled: a later Dropped from the host is
                // a correction, not the echo of that release.
                CompleteLocalVrReleaseAcknowledgement();

                // Every inventory item is in the slot the server has it in (OI-SLOT-01). An
                // essential in its claim slot goes through the claim, which keeps its silhouette;
                // anything else goes to its slot exactly. Left in the world, the item was reported
                // dropped, the host sent it back to the inventory, and so on every update.
                // A refused claim isn't a placement, reserved or not: the item goes to the first free
                // slot rather than in none (OI-START-10).
                bool inClaim = snapshot.InventoryClaimSlot >= 0 &&
                    (snapshot.InventorySlot < 0 || snapshot.InventorySlot == snapshot.InventoryClaimSlot);
                bool placed = inClaim ? RestoreLocalInventoryClaim(snapshot)
                    : snapshot.InventorySlot >= 0 && InventoryIntegration.PlaceAt(gameObject, snapshot.InventorySlot) >= 0;
                if (!placed && InventoryIntegration.AddActive(gameObject) < 0)
                    PublishClaimInvariant("local-inventory-placement-failed", -1,
                        ItemState.InInventory);
            }
            else if (plan.Activate)
                gameObject.SetActive(true);

            // A refused stash (a station fixture, owned-items PR 1c): the host keeps it in our hand,
            // but DV already put it in our inventory, and applying "in your hand" alone left it there,
            // so we sent the stash again and again. It drops at our feet; the drop we then send is
            // one the host takes, and both games agree.
            if (plan.Action == RemoteItemProjectionAction.LocalHand &&
                snapshot.MoveCause?.Reason == ItemMoveReason.HostCorrection &&
                InventoryIntegration.ContainsActive(gameObject) && Item?.IsGrabbed() != true)
                DV.InventorySystem.Inventory.Instance?.DropItemFromHandsOrInventory(gameObject);

            DebugRuntime.Publish("item", snapshot.TransitionReason == ItemTransitionReason.OwnerRecall
                    ? "item.recall-applied" : "item.local-authority-projection-applied",
                NetworkRole.DebugSide,
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
            playerBelongsTo.HoldItem(gameObject, pos, rot, snapshot.PlayerHand);
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
            // A joining player's saved items are applied before the player exists here, one line
            // per item on every login, and are sent again once they've loaded: not a fault. An
            // item in nobody's hand or inventory is.
            if (snapshot.PlayerId == 0)
                Multiplayer.LogWarning($"Could not project item {NetId} to player 0 (no player); disabling");
            else
                Multiplayer.LogDebug(() => $"Could not project item {NetId} to player {snapshot.PlayerId} (not here yet); disabling");
            if (plan.Deactivate)
                gameObject.SetActive(false);
        }
    }

    private bool RestoreLocalInventoryClaim(ItemUpdate snapshot)
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
            NetworkRole.DebugSide,
            entityType: "Item", entityId: NetId.ToString(), data: new()
            {
                ["requestedSlot"] = requestedSlot,
                ["restoredSlot"] = execution.CurrentSlot,
                ["plan"] = plan.Action.ToString(),
                ["reserved"] = execution.Reserved,
                ["dropped"] = execution.Dropped,
                ["relation"] = Relation.ToString(),
                ["authorityRevision"] = AuthorityRevision
            });
        return true;
    }

    internal void ApplyAuthorityMetadata(ItemUpdate snapshot)
    {
        if (snapshot == null)
            return;
        AuthorityRevision = snapshot.AuthorityRevision;
        ClientPart?.ObserveCanonicalRevision(snapshot);
        Relation = snapshot.Relation;
        InventoryClaimSlot = snapshot.InventoryClaimSlot;
        InventoryClaimFlags = snapshot.InventoryClaimFlags;
        LastTransitionReason = snapshot.TransitionReason;
        ApplyPlayerHandMetadata(snapshot);
        // Ownership is the authoritative multiplayer classification for a personal item. A
        // retrieval claim is a separate capability: an ordinary owned item still belongs to its
        // player and must remain eligible for Lost and Found.
        if (Relation.IsOwned() && Item?.InventorySpecs != null)
            Item.InventorySpecs.BelongsToPlayer = true;
    }

    private void PublishHolderInvariant(ItemUpdate snapshot)
    {
        byte localPlayerId = InventoryIntegration.LocalPlayerId;
        NetworkedPlayer actualRemoteHolder = null;
        if (NetworkLifecycle.Instance.Avatars != null)
            actualRemoteHolder = NetworkLifecycle.Instance.Avatars.Players
                .FirstOrDefault(player => player?.HoldsItem(gameObject) == true);
        List<string> violations = new();
        if (snapshot.ItemState == ItemState.InHand && snapshot.PlayerId != localPlayerId &&
            actualRemoteHolder?.PlayerId != snapshot.PlayerId)
            violations.Add("expected-remote-hand-missing");
        if (snapshot.ItemState != ItemState.InHand && actualRemoteHolder != null)
            violations.Add("stale-remote-hand-membership");
        if (NetworkRole.IsServer && HostPossessorPlayerId != snapshot.PlayerId)
            violations.Add("host-possessor-mismatch");
        if (violations.Count == 0)
            return;
        DebugRuntime.Publish("item", "item.holder-invariant-violation",
            NetworkRole.DebugSide,
            DebugSeverity.Warning, "Item", NetId.ToString(), new()
            {
                ["expectedState"] = snapshot.ItemState.ToString(),
                ["expectedPlayerId"] = snapshot.PlayerId,
                ["localPlayerId"] = localPlayerId,
                ["actualRemoteHolderPlayerId"] = actualRemoteHolder?.PlayerId ?? 0,
                ["hostPossessorPlayerId"] = HostPossessorPlayerId,
                ["violations"] = violations
            });
    }

    private void CaptureLocalInventoryClaim(ItemUpdate snapshot)
    {
        if (snapshot == null)
            return;
        byte localPlayerId = InventoryIntegration.LocalPlayerId;
        if (localPlayerId == 0 || Relation == ItemRelation.SomeoneElses)
            return;
        try
        {
            DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
            int slot = inventory?.IndexOf(gameObject) ?? -1;
            // The slot it's in, for every item: the server keeps it (OI-SLOT-01).
            snapshot.InventorySlot = slot;
            // Derail Valley's item-getter button is specifically gated by
            // InventorySlotDisplayData.IsItemGetter, which is copied from IsEssential.
            // Reservation is also used for ordinary inventory bookkeeping and must not
            // grant those items recall silhouettes or foreign-item styling.
            if (slot < 0 || Item?.InventorySpecs?.IsEssential != true)
                return;
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
            NetworkRole.DebugSide,
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


    internal static bool HasRetrievalFlag(ItemInventoryClaimFlags flags) =>
        (flags & (ItemInventoryClaimFlags.Reserved | ItemInventoryClaimFlags.Locked)) != 0;
}
