using DV.CabControls.VRTK;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Items;
using Multiplayer.Networking.Data.Items;
using UnityEngine;
using VRTK;

namespace Multiplayer.Components.Networking.World;

/// <summary>VR-specific hand observation and canonical metadata for networked items.</summary>
public partial class NetworkedItem
{
    private PlayerHand playerHand;
    private PlayerHand lastLocallyReleasedHand;

    internal PlayerHand PlayerHand => playerHand;

    private PlayerHand HandForState(ItemState state)
    {
        if (state != ItemState.InHand)
            return PlayerHand.None;

        PlayerHand observed = ObserveLocalVrHand();
        if (observed != PlayerHand.None)
            playerHand = observed;
        return playerHand;
    }

    private PlayerHand ObserveLocalVrHand()
    {
        if (Item is not ItemVRTK vrItem || !vrItem.IsGrabbed())
            return PlayerHand.None;

        GameObject grabbingObject = vrItem.GetGrabbingObject();
        if (grabbingObject == null)
            return PlayerHand.None;

        GameObject left = VRTK_DeviceFinder.GetControllerLeftHand(false);
        if (IsControllerObject(grabbingObject, left))
            return PlayerHand.Left;

        GameObject right = VRTK_DeviceFinder.GetControllerRightHand(false);
        return IsControllerObject(grabbingObject, right) ? PlayerHand.Right : PlayerHand.None;
    }

    private static bool IsControllerObject(GameObject grabbingObject, GameObject controller)
    {
        if (grabbingObject == null || controller == null)
            return false;
        return grabbingObject == controller ||
            grabbingObject.transform.IsChildOf(controller.transform) ||
            controller.transform.IsChildOf(grabbingObject.transform);
    }

    private void RefreshLocalVrHandAfterInteraction()
    {
        PlayerHand observed = ObserveLocalVrHand();
        if (observed != PlayerHand.None)
        {
            playerHand = observed;
            lastLocallyReleasedHand = PlayerHand.None;
            return;
        }

        if (playerHand != PlayerHand.None)
            lastLocallyReleasedHand = playerHand;
        playerHand = PlayerHand.None;
    }

    private void ApplyPlayerHandMetadata(ItemUpdateData snapshot)
    {
        if (!ItemUpdateData.IncludesItemState(snapshot.UpdateType))
            return;
        playerHand = snapshot.ItemState == ItemState.InHand
            ? PlayerHandRules.Normalize(snapshot.PlayerHand)
            : PlayerHand.None;
    }

    private bool IsLocalVrReleaseAcknowledgement(ItemUpdateData snapshot)
    {
        byte localPlayerId = NetworkLifecycle.Instance.Client?.PlayerId ?? 0;
        return ItemSnapshotApplicationPolicy.IsLocalVrReleaseAcknowledgement(
            NetworkLifecycle.Instance.IsHost(), localPlayerId, snapshot.OriginatingPlayerId,
            WireItemState.InHand, ToWireState(snapshot.ItemState),
            (byte)lastLocallyReleasedHand);
    }

    private void CompleteLocalVrReleaseAcknowledgement()
    {
        lastLocallyReleasedHand = PlayerHand.None;
        playerHand = PlayerHand.None;
    }

    private void ApplyLocalVrReleaseAcknowledgement(ItemUpdateData snapshot)
    {
        // VRTK has already called its grab-attach mechanic and assigned the controller's
        // native linear/angular velocity. Treat the host echo as an acknowledgement instead
        // of replaying Dropped, which would zero those velocities in state application.
        lastState = ItemState.Dropped;
        lastSentProjection = ItemWireStateComparer.StableBaselineAfterSend(
            ProjectionFromSnapshot(snapshot));
        hasLastSentState = true;
        createdDirty = false;
        stateDirty = false;
        wasThrown = false;
        hostStateObservationPending = false;
        MarkValuesClean();
        CompleteLocalVrReleaseAcknowledgement();
        EntityDebugRegistry.UpdateState("Item", NetId.ToString(),
            EntityDebugRegistry.ItemState(this));
        DebugRuntime.Publish("item", "item.local-vr-release-acknowledged",
            DebugRuntimeSide.Client, entityType: "Item", entityId: NetId.ToString(), data: new()
            {
                ["authorityRevision"] = snapshot.AuthorityRevision,
                ["velocityPreserved"] = DebugValueSnapshotter.Snapshot(Item?.ItemRigidbody?.velocity),
                ["angularVelocityPreserved"] = DebugValueSnapshotter.Snapshot(Item?.ItemRigidbody?.angularVelocity)
            });
    }

    private void ClearPlayerHand()
    {
        playerHand = PlayerHand.None;
        lastLocallyReleasedHand = PlayerHand.None;
    }

    private void ResetVrHandState() => ClearPlayerHand();
}
