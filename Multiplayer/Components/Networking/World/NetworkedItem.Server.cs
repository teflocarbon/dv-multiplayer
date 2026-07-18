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
    // Host validation and canonical logical transitions.
    internal void ServerInitialiseAdoptedItem(ServerPlayer owner, ItemUpdateData snapshot)
    {
        if (owner == null || NetId == 0 || snapshot == null)
            return;

        snapshot.ItemNetId = NetId;
        snapshot.UpdateType = ItemUpdateData.ItemUpdateType.FullSync;
        snapshot.PersistentOwnerPlayerId = owner.PlayerId;
        if (!AuthoritativeItemRegistry.TryApplyTransition(this, snapshot, owner,
                ItemTransitionReason.ClientAdoption, true, out string rejectionReason))
        {
            Multiplayer.LogWarning($"Unable to initialize adopted item {NetId}: {rejectionReason}");
            return;
        }
        UpdateHostPossessor(snapshot.PlayerId);
        ApplyAuthorityMetadata(snapshot);
        if (snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) && Item?.InventorySpecs != null)
            Item.InventorySpecs.BelongsToPlayer = snapshot.BelongsToPlayer;
        ReceiveSnapshot(snapshot);
        // TEMPORARY compatibility adoption can change a newly registered projection from a
        // spatial world item into a player-held item before the normal dirty-item pass runs.
        // Remove this path after all producers use explicit host-authoritative operations.
        NetworkedItemManager.Instance?.RegisterHostWorldItem(this);
    }

    public void Server_ReceiveItemUpdate(ItemUpdateData snapshot, ServerPlayer senderPlayer)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(snapshot, DebugRuntimeSide.Server, "Item", snapshot?.ItemNetId.ToString());
        if (NetworkedLostAndFoundManager.Contains(NetId))
        {
            DebugTrace.Validation("item", "Item", NetId.ToString(), false,
                "item-in-lost-and-found", DebugRuntimeSide.Server);
            DebugRuntime.Publish("item", "item.lost-and-found-live-transition-rejected",
                DebugRuntimeSide.Server, DebugSeverity.Warning, "Item", NetId.ToString(), new()
                {
                    ["senderPlayerId"] = senderPlayer?.PlayerId ?? 0,
                    ["requestedState"] = snapshot?.ItemState.ToString() ?? string.Empty,
                    ["requestedRevision"] = snapshot?.AuthorityRevision ?? 0
                });
            SendCanonicalRejectionCorrection(senderPlayer, "item-in-lost-and-found");
            return;
        }
        //TODO: rollback if validation fails
        if (!ValidateUpdate(snapshot, senderPlayer))
        {
            SendCanonicalRejectionCorrection(senderPlayer, "snapshot-validation-rejected");
            return;
        }

        // The authenticated transport sender is authoritative for this envelope field. It lets
        // the sender consume the canonical revision as an acknowledgement without performing its
        // already-running local throw a second time.
        snapshot.OriginatingPlayerId = senderPlayer.PlayerId;

        if (!AuthoritativeItemRegistry.TryApplyTransition(this, snapshot, senderPlayer,
                ItemTransitionReason.ClientState, false, out string authorityRejection))
        {
            DebugTrace.Validation("item", "Item", NetId.ToString(), false, authorityRejection, DebugRuntimeSide.Server);
            SendCanonicalRejectionCorrection(senderPlayer, authorityRejection);
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

        // Placement is part of canonical authority, so the host interest index must change in
        // the same operation. Waiting for ProcessChanged leaves a window where inventory/hand
        // items remain indexed in their previous world cell (or thrown items remain non-spatial).
        NetworkedItemManager.Instance?.RegisterHostWorldItem(this);

        //NetworkedItemManager.Instance.AddDirtyItemSnapshot(this, snapshot);
        // The sender also receives the canonical revision/owner projection. Applying an
        // acknowledgement is idempotent and prevents the next transition using a stale revision.
        NetworkLifecycle.Instance.Server.SendItemUpdatePacket(snapshot);
    }

    private void SendCanonicalRejectionCorrection(ServerPlayer senderPlayer, string rejectionReason)
    {
        if (senderPlayer == null || NetworkLifecycle.Instance?.Server == null)
            return;
        bool lostAndFoundProjection = NetworkedLostAndFoundManager.TryCreateCollectionProjection(
            this, out ItemUpdateData correction);
        if (!lostAndFoundProjection &&
            !AuthoritativeItemRegistry.TryCreateCorrectionSnapshot(this, out correction))
            return;

        DebugRuntime.Publish("item", "item.rejection-correction-sent", DebugRuntimeSide.Server,
            DebugSeverity.Warning, "Item", NetId.ToString(), new()
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

    private bool ValidateUpdate(ItemUpdateData snapshot, ServerPlayer senderPlayer)
    {
        if (snapshot == null || senderPlayer == null)
            return false;
        // Clients can not spawn or destroy items
        if (snapshot.UpdateType.HasAnyFlag(ItemUpdateData.ItemUpdateType.Create | ItemUpdateData.ItemUpdateType.Destroy))
        {
            DebugTrace.Validation("item", "Item", NetId.ToString(), false, "client-create-or-destroy-not-allowed", DebugRuntimeSide.Server);
            return false;
        }
        if ((ItemUpdateData.IncludesItemState(snapshot.UpdateType) ||
             snapshot.UpdateType.HasAnyFlag(ItemUpdateData.ItemUpdateType.ItemPosition)) &&
            (!IsFinite(snapshot.ItemPosition) || !IsFinite(snapshot.ItemRotation) ||
             !IsFinite(snapshot.ThrowDirection) || !IsFinite(snapshot.ParentLocalPosition) ||
             !IsFinite(snapshot.ParentLocalRotation)))
        {
            DebugTrace.Validation("item", "Item", NetId.ToString(), false,
                "invalid-item-transform", DebugRuntimeSide.Server);
            return false;
        }

        bool senderPossesses = AuthoritativeItemRegistry.TryGet(NetId, out AuthoritativeItemRegistry.Record authority) &&
            authority.PlacementPlayerId == senderPlayer.PlayerId &&
            authority.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory;
        if (!senderPossesses && !senderPlayer.AcknowledgedWorldItems.Contains(NetId))
        {
            DebugTrace.Validation("item", "Item", NetId.ToString(), false,
                "item-not-projected-to-sender", DebugRuntimeSide.Server);
            return false;
        }

        // A projection acknowledgement permits the initial world -> hand interaction only.
        // Once placement is not changing, tracked-state and position mutations require current
        // possession; merely seeing a nearby object is not an authority grant.
        if (!senderPossesses && !ItemUpdateData.IncludesItemState(snapshot.UpdateType) &&
            snapshot.UpdateType.HasAnyFlag(ItemUpdateData.ItemUpdateType.ObjectState |
                                            ItemUpdateData.ItemUpdateType.ItemPosition))
        {
            DebugTrace.Validation("item", "Item", NetId.ToString(), false,
                "sender-not-current-possessor", DebugRuntimeSide.Server);
            return false;
        }
        if (!senderPossesses && ItemUpdateData.IncludesItemState(snapshot.UpdateType) &&
            snapshot.ItemState is not (ItemState.InHand or ItemState.InInventory))
        {
            DebugTrace.Validation("item", "Item", NetId.ToString(), false,
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
                DebugTrace.Validation("item", "Item", NetId.ToString(), false,
                    "world-placement-outside-sender-bound", DebugRuntimeSide.Server);
                return false;
            }
            if (snapshot.WorldParentKind == ItemWorldParentKind.TrainInterior &&
                (!NetworkedTrainCar.TryGet(snapshot.WorldParentNetId, out TrainCar parentCar) ||
                 (parentCar.transform.position - WorldMover.currentMove - senderPlayer.AbsoluteWorldPosition).sqrMagnitude > 192f * 192f))
            {
                DebugTrace.Validation("item", "Item", NetId.ToString(), false,
                    "invalid-or-distant-train-parent", DebugRuntimeSide.Server);
                return false;
            }
            if (snapshot.WorldParentKind == ItemWorldParentKind.StaticParent &&
                (!WorldItemStaticParentRegistry.TryResolve(snapshot.WorldParentKey, out Transform staticParent) ||
                 (staticParent.position - WorldMover.currentMove - senderPlayer.AbsoluteWorldPosition).sqrMagnitude > 192f * 192f))
            {
                DebugTrace.Validation("item", "Item", NetId.ToString(), false,
                    "invalid-or-distant-static-parent", DebugRuntimeSide.Server);
                return false;
            }
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

    private static bool IsFinite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);

    private static bool IsFinite(Quaternion value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z) &&
        !float.IsNaN(value.w) && !float.IsInfinity(value.w);

    internal void ApplyHostLocalCanonicalTransition(ItemUpdateData snapshot, ServerPlayer actor)
    {
        if (snapshot == null || actor == null)
            return;
        PublishUnityState("item.ownership-transition.before", new()
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
        UpdateHostPossessor(snapshot.PlayerId);
        ApplyAuthorityMetadata(snapshot);
        PublishUnityState("item.ownership-transition.after", new()
        {
            ["actorPlayerId"] = actor.PlayerId,
            ["targetState"] = snapshot.ItemState.ToString(),
            ["transitionReason"] = snapshot.TransitionReason.ToString(),
            ["unityStateAlreadyApplied"] = true
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
}
