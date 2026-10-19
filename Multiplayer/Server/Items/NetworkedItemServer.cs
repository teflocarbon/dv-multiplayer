using DV;
using DV.CabControls;
using DV.Customization.Gadgets;
using DV.Interaction;
using DV.InventorySystem;
using DV.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Collections;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items;

namespace Multiplayer.Server.Items;

/// <summary>
/// The server's part of <see cref="NetworkedItem"/>: validating a player's change to an item,
/// committing it to the authority record, refusing it with a correction, and applying the server's
/// own canonical snapshots to the item.
/// </summary>
public static class NetworkedItemServer
{
    internal static void ServerInitialiseAdoptedItem(this NetworkedItem item, ServerPlayer owner, ItemUpdateData snapshot)
    {
        if (owner == null || item.NetId == 0 || snapshot == null)
            return;

        snapshot.ItemNetId = item.NetId;
        snapshot.UpdateType = ItemUpdateData.ItemUpdateType.FullSync;
        snapshot.PersistentOwnerPlayerId = owner.PlayerId;
        // Registered already in the state it arrives in, so name the adoption: nothing else would.
        if (!AuthoritativeItemRegistry.TryApplyTransition(item, snapshot, owner,
                ItemTransitionReason.ClientAdoption, true, out string rejectionReason,
                cause: ItemChangeLog.Move(ItemMoveReason.Adopted, owner,
                    $"made by their game as {snapshot.ItemState}, owner {owner.Username}")))
        {
            Multiplayer.LogWarning($"Unable to initialize adopted item {item.NetId}: {rejectionReason}");
            return;
        }
        item.UpdateHostPossessor(snapshot.PlayerId);
        item.ApplyAuthorityMetadata(snapshot);
        if (snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) && item.Item?.InventorySpecs != null)
            item.Item.InventorySpecs.BelongsToPlayer = snapshot.BelongsToPlayer;
        item.ReceiveSnapshot(snapshot);
        // TEMPORARY compatibility adoption can change a newly registered projection from a
        // spatial world item into a player-held item before the normal dirty-item pass runs.
        // Remove this path after all producers use explicit host-authoritative operations.
        ServerItemManager.Instance?.RegisterHostWorldItem(item);
    }

    public static void Server_ReceiveItemUpdate(this NetworkedItem item, ItemUpdateData snapshot, ServerPlayer senderPlayer)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(snapshot, DebugRuntimeSide.Server, "Item", snapshot?.ItemNetId.ToString());
        if (NetworkedLostAndFoundManager.Contains(item.NetId))
        {
            DebugTrace.Validation("item", "Item", item.NetId.ToString(), false,
                "item-in-lost-and-found", DebugRuntimeSide.Server);
            DebugRuntime.Publish("item", "item.lost-and-found-live-transition-rejected",
                DebugRuntimeSide.Server, DebugSeverity.Warning, "Item", item.NetId.ToString(), new()
                {
                    ["senderPlayerId"] = senderPlayer?.PlayerId ?? 0,
                    ["requestedState"] = snapshot?.ItemState.ToString() ?? string.Empty,
                    ["requestedRevision"] = snapshot?.AuthorityRevision ?? 0
                });
            item.SendCanonicalRejectionCorrection(senderPlayer, "item-in-lost-and-found");
            return;
        }
        if (item.IsIgnoredNonHolderObjectState(snapshot, senderPlayer))
        {
            // Not a dispute about where the item is, so no correction: its position put a thrown
            // item back where it was let go, and set off the sender's copy again (playtest 2).
            DebugTrace.Validation("item", "Item", item.NetId.ToString(), false,
                "non-holder-object-state-ignored", DebugRuntimeSide.Server);
            return;
        }
        //TODO: rollback if validation fails
        if (!item.ValidateUpdate(snapshot, senderPlayer))
        {
            item.SendCanonicalRejectionCorrection(senderPlayer, "snapshot-validation-rejected");
            return;
        }

        // The authenticated transport sender is authoritative for this envelope field. It lets
        // the sender consume the canonical revision as an acknowledgement without performing its
        // already-running local throw a second time.
        snapshot.OriginatingPlayerId = senderPlayer.PlayerId;

        bool senderHeldIt = item.SenderPossesses(senderPlayer);
        // Working an item where it lies changes its state, not where it is: the record keeps its
        // pose (a physics lease or the last settlement set it), not the sender's copy's.
        if (!senderHeldIt && item.IsAcceptedWorldInteraction(snapshot, senderPlayer))
            AuthoritativeItemRegistry.KeepRecordedPose(item, snapshot);
        if (!AuthoritativeItemRegistry.TryApplyTransition(item, snapshot, senderPlayer,
                ItemTransitionReason.ClientState, false, out string authorityRejection))
        {
            DebugTrace.Validation("item", "Item", item.NetId.ToString(), false, authorityRejection, DebugRuntimeSide.Server);
            item.SendCanonicalRejectionCorrection(senderPlayer, authorityRejection);
            return;
        }
        // The player who let go of it has it in their world, whether or not they were ever sent
        // it there (a /give, a starting item): their next grab of it is a pickup like anyone's.
        if (senderHeldIt && !item.SenderPossesses(senderPlayer))
            senderPlayer.AcknowledgedWorldItems.Add(item.NetId);
        DebugTrace.Validation("item", "Item", item.NetId.ToString(), true, "accepted", DebugRuntimeSide.Server);

        Multiplayer.LogDebug(() => $"NetworkedItem.Server_ReceiveItemUpdate() NetId: {snapshot?.ItemNetId}, ItemState: {snapshot?.ItemState}, Player: {senderPlayer.DisplayName}");
        // FullSync is a composite flag (ItemState | ItemPosition | ObjectState).
        // Including it in a HasAnyFlag mask also matches a plain ObjectState packet,
        // incorrectly turning tracked-value-only updates into hand/world transitions.
        bool appliesItemState = ItemUpdateData.IncludesItemState(snapshot.UpdateType);
        if (appliesItemState)
        {
            item.UpdateHostPossessor(snapshot.PlayerId);
        }

        // Allow host to process packet if not from a local client
        if (!NetworkLifecycle.Instance.IsClientRunning || (NetworkLifecycle.Instance.IsClientRunning && senderPlayer != NetworkLifecycle.Instance.Server.LocalPlayer))
        {
            item.ProcessingAsHost = true;
            item.ReceiveSnapshot(snapshot);
        }

        // Placement is part of canonical authority, so the host interest index must change in
        // the same operation. Waiting for ProcessChanged leaves a window where inventory/hand
        // items remain indexed in their previous world cell (or thrown items remain non-spatial).
        ServerItemManager.Instance?.RegisterHostWorldItem(item);

        //NetworkedItemManager.Instance.AddDirtyItemSnapshot(item, snapshot);
        // The sender also receives the canonical revision/owner projection. Applying an
        // acknowledgement is idempotent and prevents the next transition using a stale revision.
        NetworkLifecycle.Instance.Server.SendItemUpdatePacket(snapshot);
    }

    private static void SendCanonicalRejectionCorrection(this NetworkedItem item, ServerPlayer senderPlayer, string rejectionReason)
    {
        if (senderPlayer == null || NetworkLifecycle.Instance?.Server == null)
            return;
        bool lostAndFoundProjection = NetworkedLostAndFoundManager.TryCreateCollectionProjection(
            item, out ItemUpdateData correction);
        if (!lostAndFoundProjection &&
            !AuthoritativeItemRegistry.TryCreateCorrectionSnapshot(item, out correction,
                ItemChangeLog.Move(ItemMoveReason.HostCorrection, detail: "your change was refused: " + rejectionReason)))
            return;
        Multiplayer.LogDebug(() => $"Item {item.name} (netId {item.NetId}): refused {senderPlayer.Username}'s change ({rejectionReason}), sent a correction");

        DebugRuntime.Publish("item", "item.rejection-correction-sent", DebugRuntimeSide.Server,
            DebugSeverity.Warning, "Item", item.NetId.ToString(), new()
            {
                ["recipientPlayerId"] = senderPlayer.PlayerId,
                ["rejectionReason"] = rejectionReason ?? string.Empty,
                ["authorityRevision"] = correction.AuthorityRevision,
                ["canonicalState"] = correction.ItemState.ToString(),
                ["correctionUpdateType"] = correction.UpdateType.ToString(),
                ["lostAndFoundProjection"] = lostAndFoundProjection,
                ["requestedCorrection"] = true
            });
        NetworkLifecycle.Instance.Server.SendItemUpdatePacket(correction, senderPlayer);
    }

    private static bool ValidateUpdate(this NetworkedItem item, ItemUpdateData snapshot, ServerPlayer senderPlayer)
    {
        if (snapshot == null || senderPlayer == null)
            return false;
        // Clients can not spawn or destroy items
        if (snapshot.UpdateType.HasAnyFlag(ItemUpdateData.ItemUpdateType.Create | ItemUpdateData.ItemUpdateType.Destroy))
        {
            DebugTrace.Validation("item", "Item", item.NetId.ToString(), false, "client-create-or-destroy-not-allowed", DebugRuntimeSide.Server);
            return false;
        }
        if ((ItemUpdateData.IncludesItemState(snapshot.UpdateType) ||
             snapshot.UpdateType.HasAnyFlag(ItemUpdateData.ItemUpdateType.ItemPosition)) &&
            (!IsFinite(snapshot.ItemPosition) || !IsFinite(snapshot.ItemRotation) ||
             !IsFinite(snapshot.ThrowDirection) || !IsFinite(snapshot.ParentLocalPosition) ||
             !IsFinite(snapshot.ParentLocalRotation)))
        {
            DebugTrace.Validation("item", "Item", item.NetId.ToString(), false,
                "invalid-item-transform", DebugRuntimeSide.Server);
            return false;
        }

        if (!PlayerHandRules.IsDefined(snapshot.PlayerHand) ||
            ItemUpdateData.IncludesItemState(snapshot.UpdateType) &&
            snapshot.ItemState != ItemState.InHand && snapshot.PlayerHand != PlayerHand.None)
        {
            DebugTrace.Validation("item", "Item", item.NetId.ToString(), false,
                "invalid-player-hand", DebugRuntimeSide.Server);
            return false;
        }

        AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record authority);
        bool senderPossesses = item.SenderPossesses(senderPlayer);
        if (!senderPossesses && !senderPlayer.AcknowledgedWorldItems.Contains(item.NetId))
        {
            DebugTrace.Validation("item", "Item", item.NetId.ToString(), false,
                "item-not-projected-to-sender", DebugRuntimeSide.Server);
            return false;
        }

        // A projection acknowledgement permits the initial world -> hand interaction, a book's
        // pages, and a nearby player working the item where it lies (its game marks that). Any
        // other tracked-state or position change needs possession: merely seeing a nearby object
        // is not an authority grant.
        bool allowedInteraction = IsPageOnlyInteraction(snapshot) && authority != null &&
            authority.Placement is not (ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory) ||
            item.IsAcceptedWorldInteraction(snapshot, senderPlayer);
        if (!senderPossesses && !allowedInteraction &&
            !ItemUpdateData.IncludesItemState(snapshot.UpdateType) &&
            snapshot.UpdateType.HasAnyFlag(ItemUpdateData.ItemUpdateType.ObjectState |
                                            ItemUpdateData.ItemUpdateType.ItemPosition))
        {
            DebugTrace.Validation("item", "Item", item.NetId.ToString(), false,
                "sender-not-current-possessor", DebugRuntimeSide.Server);
            return false;
        }
        if (!senderPossesses && ItemUpdateData.IncludesItemState(snapshot.UpdateType) &&
            snapshot.ItemState is not (ItemState.InHand or ItemState.InInventory))
        {
            DebugTrace.Validation("item", "Item", item.NetId.ToString(), false,
                "world-transition-requires-possession", DebugRuntimeSide.Server);
            return false;
        }

        if (ItemUpdateData.IncludesItemState(snapshot.UpdateType) &&
            snapshot.ItemState is ItemState.Dropped or ItemState.Thrown)
        {
            // This is a logical placement sanity bound, not the future network-physics policy.
            // A client may throw nearby, but cannot relocate a projected item across the map.
            if ((snapshot.ItemPosition - senderPlayer.AbsoluteWorldPosition).sqrMagnitude > 64f * 64f)
            {
                DebugTrace.Validation("item", "Item", item.NetId.ToString(), false,
                    "world-placement-outside-sender-bound", DebugRuntimeSide.Server);
                return false;
            }
            if (snapshot.WorldParentKind == ItemWorldParentKind.TrainInterior &&
                (!NetworkedTrainCar.TryGet(snapshot.WorldParentNetId, out TrainCar parentCar) ||
                 (parentCar.transform.position - WorldMover.currentMove - senderPlayer.AbsoluteWorldPosition).sqrMagnitude > 192f * 192f))
            {
                DebugTrace.Validation("item", "Item", item.NetId.ToString(), false,
                    "invalid-or-distant-train-parent", DebugRuntimeSide.Server);
                return false;
            }
            if (snapshot.WorldParentKind == ItemWorldParentKind.StaticParent &&
                (!WorldItemStaticParentRegistry.TryResolve(snapshot.WorldParentKey, out Transform staticParent) ||
                 (staticParent.position - WorldMover.currentMove - senderPlayer.AbsoluteWorldPosition).sqrMagnitude > 192f * 192f))
            {
                DebugTrace.Validation("item", "Item", item.NetId.ToString(), false,
                    "invalid-or-distant-static-parent", DebugRuntimeSide.Server);
                return false;
            }
        }

        if (snapshot.States != null && snapshot.States.TryGetValue(NetworkedPageBookState.TrackedValueKey, out object pageValue))
        {
            if (pageValue is not int requestedPage)
            {
                DebugTrace.Validation("item", "Item", item.NetId.ToString(), false, "page-index-type-invalid", DebugRuntimeSide.Server);
                return false;
            }
            PageBook pageBook = item.GetComponentInChildren<PageBook>(true);
            if (pageBook == null)
            {
                DebugTrace.Validation("item", "Item", item.NetId.ToString(), false, "pagebook-not-present", DebugRuntimeSide.Server);
                return false;
            }
            PageBookApplyPlan pagePlan = PageBookSyncPlanner.Plan(requestedPage, pageBook.PagesGenerated, pageBook.PageNum);
            if (pagePlan.Status == PageBookApplyStatus.Reject)
            {
                DebugTrace.Validation("item", "Item", item.NetId.ToString(), false, pagePlan.Reason, DebugRuntimeSide.Server);
                return false;
            }
        }

        return true;
    }

    private static bool SenderPossesses(this NetworkedItem item, ServerPlayer senderPlayer) =>
        senderPlayer != null &&
        AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record authority) &&
        authority.PlacementPlayerId == senderPlayer.PlayerId &&
        authority.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory;

    private static bool IsIgnoredNonHolderObjectState(this NetworkedItem item, ItemUpdateData snapshot, ServerPlayer senderPlayer) =>
        snapshot != null && senderPlayer != null &&
        ItemObjectStateAuthority.HostIgnores(
            ItemUpdateData.IncludesItemState(snapshot.UpdateType),
            snapshot.UpdateType.HasAnyFlag(ItemUpdateData.ItemUpdateType.ItemPosition),
            snapshot.UpdateType.HasAnyFlag(ItemUpdateData.ItemUpdateType.ObjectState),
            snapshot.UpdateType.HasAnyFlag(ItemUpdateData.ItemUpdateType.Create | ItemUpdateData.ItemUpdateType.Destroy),
            item.SenderPossesses(senderPlayer),
            // Anyone may turn the pages of a book lying in the world; not one in someone's hand.
            IsPageOnlyInteraction(snapshot) &&
            !(AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record authority) &&
              authority.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory) ||
            item.IsAcceptedWorldInteraction(snapshot, senderPlayer));

    /// <summary>
    /// A nearby player working an item where it lies (turning a set-down lantern's knob, lighting it
    /// with their lighter): their game marks the change as an interaction, and it's taken as object
    /// state only. Unmarked changes from other games' drifting copies are still ignored.
    /// </summary>
    private static bool IsAcceptedWorldInteraction(this NetworkedItem item, ItemUpdateData snapshot, ServerPlayer senderPlayer)
    {
        if (snapshot == null || senderPlayer == null ||
            snapshot.UpdateType != ItemUpdateData.ItemUpdateType.ObjectState ||
            !AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record authority))
            return false;
        bool itemInWorld = authority.Placement is ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
            ItemPlacementKind.StaticParent or ItemPlacementKind.Attached;
        float distance = Vector3.Distance(item.transform.position - WorldMover.currentMove,
            senderPlayer.AbsoluteWorldPosition);
        return ItemObjectStateAuthority.HostAcceptsWorldInteraction(
            snapshot.TransitionReason == ItemTransitionReason.ClientInteraction,
            senderPlayer.AcknowledgedWorldItems.Contains(item.NetId), itemInWorld, distance);
    }

    private static bool IsPageOnlyInteraction(ItemUpdateData snapshot) =>
        snapshot != null &&
        snapshot.UpdateType == ItemUpdateData.ItemUpdateType.ObjectState &&
        snapshot.States is { Count: 1 } &&
        snapshot.States.ContainsKey(NetworkedPageBookState.TrackedValueKey);

    private static bool IsFinite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);

    private static bool IsFinite(Quaternion value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z) &&
        !float.IsNaN(value.w) && !float.IsInfinity(value.w);

    internal static void ApplyHostLocalCanonicalTransition(this NetworkedItem item, ItemUpdateData snapshot, ServerPlayer actor)
    {
        if (snapshot == null || actor == null)
            return;
        item.PublishUnityState("item.ownership-transition.before", new()
        {
            ["actorPlayerId"] = actor.PlayerId,
            ["targetState"] = snapshot.ItemState.ToString(),
            ["transitionReason"] = snapshot.TransitionReason.ToString(),
            ["unityStateAlreadyApplied"] = true
        });

        // GetSnapshot observes a transition the listen player's base-game Inventory/GrabHandler
        // has already completed. Re-running PrepareForStateChange here is destructive: for a
        // retained recall claim it adds the item back to Inventory and invokes the base-game drop
        // path, which zeroes an already-running local throw. Host-local projection therefore only
        // commits canonical metadata and possessor bookkeeping. Remote snapshots still use the
        // normal PrepareForStateChange/ApplySnapshot path.
        item.UpdateHostPossessor(snapshot.PlayerId);
        item.ApplyAuthorityMetadata(snapshot);
        item.PublishUnityState("item.ownership-transition.after", new()
        {
            ["actorPlayerId"] = actor.PlayerId,
            ["targetState"] = snapshot.ItemState.ToString(),
            ["transitionReason"] = snapshot.TransitionReason.ToString(),
            ["unityStateAlreadyApplied"] = true
        });
    }

    private static void UpdateHostPossessor(this NetworkedItem item, byte playerId) =>
        item.SetHostPossessor(playerId != 0 && NetworkLifecycle.Instance.Server != null &&
            NetworkLifecycle.Instance.Server.TryGetServerPlayer(playerId, out _) ? playerId : (byte)0);

    internal static void ApplyServerCanonicalSnapshot(this NetworkedItem item, ItemUpdateData snapshot)
    {
        if (snapshot == null)
            return;
        item.UpdateHostPossessor(snapshot.PlayerId);
        item.ApplyAuthorityMetadata(snapshot);
        item.ProcessingAsHost = true;
        item.ReceiveSnapshot(snapshot);
        item.ProcessingAsHost = false;
    }

    /// <summary>
    /// Host: the item is being destroyed (its OnDestroy). Logs why, keeps a scene-authored item's
    /// tombstone, queues the Destroy for the players who know it, and drops the item's record.
    /// </summary>
    internal static void OnDestroyed(this NetworkedItem item, bool suppressNetworkDestroy)
    {
        // A cold-storage retirement was logged and sent by the code that stored the item.
        ChangeCause<ItemRemovalReason>? removal = null;
        if (!suppressNetworkDestroy && NetworkLifecycle.Instance.IsHost() && item.NetId != 0 &&
            !item.ColdMaterializationPending && (!item.ColdStorageRetirement || item.PendingRemovalCause.HasValue))
        {
            // Unity destroys at the end of the frame; a note from seconds ago was for a destroy that never came.
            bool fresh = item.PendingRemovalCause.HasValue &&
                unchecked(NetworkLifecycle.Instance.Tick - item.PendingRemovalCause.Value.Tick) <= NetworkLifecycle.TICK_RATE * 2;
            removal = fresh ? item.PendingRemovalCause.Value : item.UnexplainedRemoval();
            ItemChangeLog.RecordRemoval(item, removal.Value);
        }

        if (!suppressNetworkDestroy && NetworkLifecycle.Instance.IsHost() &&
            !item.ColdStorageRetirement && !item.ColdMaterializationPending)
        {
            WorldItemPersistenceManager.RecordDestroyed(item);
            var updateData = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Destroy);
            if (updateData != null)
            {
                updateData.RemovalCause = removal;
                ServerItemManager.Instance?.AddDirtyItemSnapshot(item, updateData);
            }
        }

        AuthoritativeItemRegistry.Remove(item.NetId);
    }

    // Unity runs OnDestroy at the end of the frame, after whatever called Destroy has returned, so
    // the stack rarely names it: say what else is known (the parent it went with, its last move).
    private static ChangeCause<ItemRemovalReason> UnexplainedRemoval(this NetworkedItem item)
    {
        string parent = item.transform.parent != null ? item.transform.parent.name : "none";
        string callers = ItemChangeCauses.Callers(typeof(NetworkedItem), typeof(NetworkedItemServer), typeof(NetworkServer));
        AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record);
        return ItemChangeLog.Removal(ItemRemovalReason.DestroyedByGame, detail: ItemChangeCauses.Detail(
            "no cause recorded",
            record != null ? "was " + record.Placement + (record.PlacementPlayerId != 0 ? " of P" + record.PlacementPlayerId : "") : null,
            "parent " + parent,
            item.gameObject.scene.isLoaded ? null : "scene unloaded",
            record != null && record.LastMove.Reason != ItemMoveReason.Unknown ? "last move " + record.LastMove : null,
            string.IsNullOrEmpty(item.HostObservationTrigger) ? null : "last host trigger " + item.HostObservationTrigger,
            item.PendingRemovalCause.HasValue ? "stale note " + item.PendingRemovalCause.Value : null,
            callers == "an unknown caller" ? null : "callers " + callers));
    }
}
