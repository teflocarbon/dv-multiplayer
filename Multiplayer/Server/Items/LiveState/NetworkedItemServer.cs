using DV;
using DV.CabControls;
using DV.Customization.Gadgets;
using DV.Interaction;
using DV.InventorySystem;
using DV.Items;
using Multiplayer.Core.Items.Behaviours;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Physics;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.Behaviours;
using Multiplayer.Shared.Items.ChangeCauses;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.WorldItems;
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

namespace Multiplayer.Server.Items.LiveState;

/// <summary>
/// The server's part of <see cref="NetworkedItem"/>: validating a player's change to an item,
/// committing it to the authority record, refusing it with a correction, and applying the server's
/// own canonical snapshots to the item.
/// </summary>
public static class NetworkedItemServer
{
    /// <summary>
    /// A player's change to an item, from their request (<c>NetworkServer.Items</c>): committed and
    /// sent to everyone, or refused with a correction to the sender. Returns the refusal's detail,
    /// or null if it was committed.
    /// </summary>
    public static string Server_ReceiveItemUpdate(this NetworkedItem item, ItemUpdate snapshot, ServerPlayer senderPlayer)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(snapshot, DebugRuntimeSide.Server, "Item", snapshot?.ItemNetId.ToString());
        if (LostAndFoundServer.Contains(item.NetId))
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
            return "item-in-lost-and-found";
        }
        if (item.IsIgnoredNonHolderObjectState(snapshot, senderPlayer))
        {
            // Not a dispute about where the item is, so no correction: its position put a thrown
            // item back where it was let go, and set off the sender's copy again (playtest 2).
            DebugTrace.Validation("item", "Item", item.NetId.ToString(), false,
                "non-holder-object-state-ignored", DebugRuntimeSide.Server);
            return "non-holder-object-state-ignored";
        }
        string invalid = item.ValidateUpdate(snapshot, senderPlayer);
        if (invalid != null)
        {
            item.SendCanonicalRejectionCorrection(senderPlayer, invalid);
            return invalid;
        }

        // The authenticated transport sender is authoritative for this envelope field. It lets
        // the sender consume the canonical revision as an acknowledgement without performing its
        // already-running local throw a second time.
        snapshot.OriginatingPlayerId = senderPlayer.PlayerId;

        bool senderHeldIt = item.SenderPossesses(senderPlayer);
        // Working an item where it lies changes its state, not where it is: the record keeps its
        // pose (a physics lease or the last settlement set it), not the sender's copy's.
        if (!senderHeldIt && item.IsAcceptedWorldInteraction(snapshot, senderPlayer))
            LiveItems.KeepRecordedPose(item, snapshot);
        if (!LiveItems.TryApplyTransition(item, snapshot, senderPlayer,
                ItemTransitionReason.ClientState, false, out string authorityRejection))
        {
            DebugTrace.Validation("item", "Item", item.NetId.ToString(), false, authorityRejection, DebugRuntimeSide.Server);
            item.SendCanonicalRejectionCorrection(senderPlayer, authorityRejection);
            return string.IsNullOrEmpty(authorityRejection) ? "authority-rejected" : authorityRejection;
        }
        // The player who let go of it has it in their world, whether or not they were ever sent
        // it there (a /give, a starting item): their next grab of it is a pickup like anyone's.
        if (senderHeldIt && !item.SenderPossesses(senderPlayer))
            senderPlayer.AcknowledgedWorldItems.Add(item.NetId);
        DebugTrace.Validation("item", "Item", item.NetId.ToString(), true, "accepted", DebugRuntimeSide.Server);

        Multiplayer.LogDebug(() => $"NetworkedItem.Server_ReceiveItemUpdate() NetId: {snapshot?.ItemNetId}, ItemState: {snapshot?.ItemState}, Player: {senderPlayer.DisplayName}");
        // FullSync is a composite flag (ItemState | ObjectState).
        // Including it in a HasAnyFlag mask also matches a plain ObjectState packet,
        // incorrectly turning tracked-value-only updates into hand/world transitions.
        bool appliesItemState = ItemUpdate.IncludesItemState(snapshot.UpdateType);
        if (appliesItemState)
        {
            item.UpdateHostPossessor(snapshot.PlayerId);
        }

        // The host's own player's change has happened in this world already: only its revision is
        // new, which their next request (a second slot move) must name.
        if (senderPlayer != NetworkServer.Instance.LocalPlayer)
            item.ReceiveSnapshot(snapshot);
        else
            item.ApplyAuthorityMetadata(snapshot);

        // Placement is part of canonical authority, so the host interest index must change in
        // the same operation. Waiting for ProcessChanged leaves a window where inventory/hand
        // items remain indexed in their previous world cell (or thrown items remain non-spatial).
        ServerItemManager.Instance?.RegisterHostWorldItem(item);

        //NetworkedItemManager.Instance.AddDirtyItemSnapshot(item, snapshot);
        // The sender also receives the canonical revision/owner projection. Applying an
        // acknowledgement is idempotent and prevents the next transition using a stale revision.
        NetworkServer.Instance.SendItemUpdate(snapshot);
        return null;
    }

    internal static void SendCanonicalRejectionCorrection(this NetworkedItem item, ServerPlayer senderPlayer, string rejectionReason)
    {
        if (senderPlayer == null || NetworkServer.Instance == null)
            return;
        bool lostAndFoundProjection = LostAndFoundServer.TryCreateCollectionProjection(
            item, out ItemUpdate correction);
        if (!lostAndFoundProjection &&
            !LiveItems.TryCreateCorrectionSnapshot(item, out correction,
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
        NetworkServer.Instance.SendItemUpdate(correction, senderPlayer);
    }

    // Why a change can't be taken, or null if it can.
    private static string ValidateUpdate(this NetworkedItem item, ItemUpdate snapshot, ServerPlayer senderPlayer)
    {
        if (snapshot == null || senderPlayer == null)
            return "invalid-request";
        // Clients can not spawn or destroy items
        if (snapshot.UpdateType.HasAnyFlag(ItemUpdate.ItemUpdateType.Create | ItemUpdate.ItemUpdateType.Destroy))
            return Refused(item, "client-create-or-destroy-not-allowed");
        if (ItemUpdate.IncludesItemState(snapshot.UpdateType) &&
            (!IsFinite(snapshot.ItemPosition) || !IsFinite(snapshot.ItemRotation) ||
             !IsFinite(snapshot.ThrowDirection) || !IsFinite(snapshot.ParentLocalPosition) ||
             !IsFinite(snapshot.ParentLocalRotation)))
            return Refused(item, "invalid-item-transform");

        if (!PlayerHandRules.IsDefined(snapshot.PlayerHand) ||
            ItemUpdate.IncludesItemState(snapshot.UpdateType) &&
            snapshot.ItemState != ItemState.InHand && snapshot.PlayerHand != PlayerHand.None)
            return Refused(item, "invalid-player-hand");

        LiveItems.TryGet(item.NetId, out LiveItems.LiveItem authority);
        bool senderPossesses = item.SenderPossesses(senderPlayer);
        if (!senderPossesses && !senderPlayer.AcknowledgedWorldItems.Contains(item.NetId))
            return Refused(item, "item-not-projected-to-sender");

        // A projection acknowledgement permits the initial world -> hand interaction, a book's
        // pages, and a nearby player working the item where it lies (its game marks that). Any
        // other tracked-state or position change needs possession: merely seeing a nearby object
        // is not an authority grant.
        bool allowedInteraction = IsPageOnlyInteraction(snapshot) && authority != null &&
            authority.Placement is not (ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory) ||
            item.IsAcceptedWorldInteraction(snapshot, senderPlayer);
        if (!senderPossesses && !allowedInteraction &&
            !ItemUpdate.IncludesItemState(snapshot.UpdateType) &&
            snapshot.UpdateType.HasAnyFlag(ItemUpdate.ItemUpdateType.ObjectState))
            return Refused(item, "sender-not-current-possessor");
        if (!senderPossesses && ItemUpdate.IncludesItemState(snapshot.UpdateType) &&
            snapshot.ItemState is not (ItemState.InHand or ItemState.InInventory))
            return Refused(item, "world-transition-requires-possession");
        // A pickup is from where the server has the item, within reach of the player (OI-PHY-08).
        if (!senderPossesses && authority != null && ItemUpdate.IncludesItemState(snapshot.UpdateType) &&
            snapshot.ItemState is ItemState.InHand or ItemState.InInventory &&
            !PickupReach.Within((Core.Items.LiveState.LivePlacement)(byte)authority.Placement,
                (item.transform.position - WorldMover.currentMove - senderPlayer.AbsoluteWorldPosition).sqrMagnitude))
            return Refused(item, "pickup-out-of-reach");

        if (ItemUpdate.IncludesItemState(snapshot.UpdateType) &&
            snapshot.ItemState is ItemState.Dropped or ItemState.Thrown)
        {
            // This is a logical placement sanity bound, not the future network-physics policy.
            // A client may throw nearby, but cannot relocate a projected item across the map.
            if ((snapshot.ItemPosition - senderPlayer.AbsoluteWorldPosition).sqrMagnitude > 64f * 64f)
                return Refused(item, "world-placement-outside-sender-bound");
            if (snapshot.WorldParentKind == ItemWorldParentKind.TrainInterior &&
                (!NetworkedTrainCar.TryGet(snapshot.WorldParentNetId, out TrainCar parentCar) ||
                 (parentCar.transform.position - WorldMover.currentMove - senderPlayer.AbsoluteWorldPosition).sqrMagnitude > 192f * 192f))
                return Refused(item, "invalid-or-distant-train-parent");
            if (snapshot.WorldParentKind == ItemWorldParentKind.StaticParent &&
                (!WorldItemStaticParentRegistry.TryResolve(snapshot.WorldParentKey, snapshot.ItemPosition, out Transform staticParent) ||
                 (staticParent.position - WorldMover.currentMove - senderPlayer.AbsoluteWorldPosition).sqrMagnitude > 192f * 192f))
                return Refused(item, "invalid-or-distant-static-parent");
        }
        // A load has the same bound as a drop: into a tool near the sender, not one across the map (H6).
        if (ItemUpdate.IncludesItemState(snapshot.UpdateType) && snapshot.ItemState == ItemState.InMagazine &&
            !(authority?.Placement == ItemPlacementKind.Magazine && authority.MagazineNetId == snapshot.MagazineNetId) &&
            NetworkedItem.TryGet(snapshot.MagazineNetId, out NetworkedItem tool) && tool != null &&
            (tool.transform.position - WorldMover.currentMove - senderPlayer.AbsoluteWorldPosition).sqrMagnitude > 64f * 64f)
            return Refused(item, "magazine-outside-sender-bound");

        if (snapshot.States != null && snapshot.States.TryGetValue(NetworkedPageBookState.TrackedValueKey, out object pageValue))
        {
            if (pageValue is not int requestedPage)
                return Refused(item, "page-index-type-invalid");
            PageBook pageBook = item.GetComponentInChildren<PageBook>(true);
            if (pageBook == null)
                return Refused(item, "pagebook-not-present");
            PageBookApplyPlan pagePlan = PageBookSyncPlanner.Plan(requestedPage, pageBook.PagesGenerated, pageBook.PageNum);
            if (pagePlan.Status == PageBookApplyStatus.Reject)
                return Refused(item, pagePlan.Reason);
        }

        return null;
    }

    private static string Refused(NetworkedItem item, string reason)
    {
        DebugTrace.Validation("item", "Item", item.NetId.ToString(), false, reason, DebugRuntimeSide.Server);
        return reason;
    }

    private static bool SenderPossesses(this NetworkedItem item, ServerPlayer senderPlayer) =>
        senderPlayer != null &&
        LiveItems.TryGet(item.NetId, out LiveItems.LiveItem authority) &&
        (authority.Holder == senderPlayer.Guid &&
         authority.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory ||
         // Ammo in a magazine is its tool holder's to use up (containers.md, "Ammo in a magazine").
         authority.Placement == ItemPlacementKind.Magazine && LiveItems.TryGet(authority.MagazineNetId, out LiveItems.LiveItem tool) &&
         tool.Holder == senderPlayer.Guid && tool.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory);

    private static bool IsIgnoredNonHolderObjectState(this NetworkedItem item, ItemUpdate snapshot, ServerPlayer senderPlayer) =>
        snapshot != null && senderPlayer != null &&
        ItemObjectStateAuthority.HostIgnores(
            ItemUpdate.IncludesItemState(snapshot.UpdateType),
            snapshot.UpdateType.HasAnyFlag(ItemUpdate.ItemUpdateType.ObjectState),
            snapshot.UpdateType.HasAnyFlag(ItemUpdate.ItemUpdateType.Create | ItemUpdate.ItemUpdateType.Destroy),
            item.SenderPossesses(senderPlayer),
            // Anyone may turn the pages of a book lying in the world; not one in someone's hand.
            IsPageOnlyInteraction(snapshot) &&
            !(LiveItems.TryGet(item.NetId, out LiveItems.LiveItem authority) &&
              authority.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory) ||
            item.IsAcceptedWorldInteraction(snapshot, senderPlayer));

    /// <summary>
    /// A nearby player working an item where it lies (turning a set-down lantern's knob, lighting it
    /// with their lighter): their game marks the change as an interaction, and it's taken as object
    /// state only. Unmarked changes from other games' drifting copies are still ignored.
    /// </summary>
    private static bool IsAcceptedWorldInteraction(this NetworkedItem item, ItemUpdate snapshot, ServerPlayer senderPlayer)
    {
        if (snapshot == null || senderPlayer == null ||
            snapshot.UpdateType != ItemUpdate.ItemUpdateType.ObjectState ||
            !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem authority))
            return false;
        bool itemInWorld = authority.Placement is ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
            ItemPlacementKind.StaticParent or ItemPlacementKind.Attached;
        float distance = Vector3.Distance(item.transform.position - WorldMover.currentMove,
            senderPlayer.AbsoluteWorldPosition);
        return ItemObjectStateAuthority.HostAcceptsWorldInteraction(
            snapshot.TransitionReason == ItemTransitionReason.ClientInteraction,
            senderPlayer.AcknowledgedWorldItems.Contains(item.NetId), itemInWorld, distance);
    }

    private static bool IsPageOnlyInteraction(ItemUpdate snapshot) =>
        snapshot != null &&
        snapshot.UpdateType == ItemUpdate.ItemUpdateType.ObjectState &&
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

    // actor null: the server itself (HostItemActor.IsServer).
    internal static void ApplyHostLocalCanonicalTransition(this NetworkedItem item, ItemUpdate snapshot, ServerPlayer actor)
    {
        if (snapshot == null)
            return;
        item.PublishUnityState("item.ownership-transition.before", new()
        {
            ["actorPlayerId"] = actor?.PlayerId ?? 0,
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
            ["actorPlayerId"] = actor?.PlayerId ?? 0,
            ["targetState"] = snapshot.ItemState.ToString(),
            ["transitionReason"] = snapshot.TransitionReason.ToString(),
            ["unityStateAlreadyApplied"] = true
        });
    }

    private static void UpdateHostPossessor(this NetworkedItem item, byte playerId) =>
        item.SetHostPossessor(playerId != 0 && NetworkServer.Instance != null &&
            NetworkServer.Instance.TryGetServerPlayer(playerId, out _) ? playerId : (byte)0);

    internal static void ApplyServerCanonicalSnapshot(this NetworkedItem item, ItemUpdate snapshot)
    {
        if (snapshot == null)
            return;
        item.UpdateHostPossessor(snapshot.PlayerId);
        item.ApplyAuthorityMetadata(snapshot);
        item.ReceiveSnapshot(snapshot);
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
            ItemSaveLoad.RecordDestroyed(item);
            var updateData = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Destroy);
            if (updateData != null)
            {
                // The snapshot carries the item's last transition. A Lost and Found collection's
                // tells clients to keep an inert copy, so this, the item's end, would leave every
                // client that saw the collection holding one for good.
                if (updateData.TransitionReason is ItemTransitionReason.LostAndFoundCollection or
                    ItemTransitionReason.ContainerDeposit or ItemTransitionReason.InterestRetirement or
                    ItemTransitionReason.WentCold)
                    updateData.TransitionReason = ItemTransitionReason.Unknown;
                updateData.RemovalCause = removal;
                ServerItemManager.Instance?.AddDirtyItemSnapshot(item, updateData);
            }
        }

        LiveItems.Remove(item.NetId);
    }

    // Unity runs OnDestroy at the end of the frame, after whatever called Destroy has returned, so
    // the stack rarely names it: say what else is known (the parent it went with, its last move).
    private static ChangeCause<ItemRemovalReason> UnexplainedRemoval(this NetworkedItem item)
    {
        string parent = item.transform.parent != null ? item.transform.parent.name : "none";
        string callers = ItemChangeCauses.Callers(typeof(NetworkedItem), typeof(NetworkedItemServer), typeof(NetworkServer));
        LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record);
        return ItemChangeLog.Removal(ItemRemovalReason.DestroyedByGame, detail: ItemChangeCauses.Detail(
            "no cause recorded",
            record != null ? "was " + record.Placement + (record.Holder != Guid.Empty ? " of " + ItemChangeLog.PlayerName(record.Holder) : "") : null,
            "parent " + parent,
            item.gameObject.scene.isLoaded ? null : "scene unloaded",
            record != null && record.LastMove.Reason != ItemMoveReason.Unknown ? "last move " + record.LastMove : null,
            string.IsNullOrEmpty(item.ServerPart?.ObservationTrigger) ? null : "last host trigger " + item.ServerPart?.ObservationTrigger,
            item.PendingRemovalCause.HasValue ? "stale note " + item.PendingRemovalCause.Value : null,
            callers == "an unknown caller" ? null : "callers " + callers));
    }
}
