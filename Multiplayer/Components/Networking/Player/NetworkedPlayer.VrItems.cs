using DV;
using DV.CabControls;
using DV.CabControls.VRTK;
using DV.Interaction;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.Networking.Player;

/// <summary>Independent left/right held-item projections for a remote VR player.</summary>
public partial class NetworkedPlayer
{
    private Vector3 leftItemAnchorPosition;
    private Quaternion leftItemAnchorRotation = Quaternion.identity;
    private Vector3 rightItemAnchorPosition;
    private Quaternion rightItemAnchorRotation = Quaternion.identity;

    public bool HoldsItem(GameObject item) =>
        item != null && (RightHandItemGO == item || LeftHandItemGO == item);

    public void HoldItem(GameObject itemGo, Vector3? targetPos, Quaternion? targetRot,
        PlayerHand hand)
    {
        hand = PlayerHandRules.Normalize(hand);
        if (hand == PlayerHand.None)
        {
            HoldDesktopItem(itemGo, targetPos, targetRot);
            return;
        }

        Multiplayer.LogDebug(() => $"NetworkedPlayer.HoldVrItem({itemGo.GetPath()}) " +
            $"Player: {username}, Hand: {hand}");

        DropProjectedItem(itemGo);
        RetireDisplacedHandItem(hand, itemGo);
        PrepareHeldItem(itemGo, hand == PlayerHand.Left
            ? disabledLHItemColliders : disabledRHItemColliders);

        Transform anchor = null;
        if (itemGo.TryGetComponent(out ItemVRTK vrItem))
            anchor = hand == PlayerHand.Left ? vrItem.GrabAnchorLeft : vrItem.GrabAnchorRight;

        Vector3 anchorPosition = anchor != null
            ? itemGo.transform.InverseTransformPoint(anchor.position)
            : Vector3.zero;
        Quaternion anchorRotation = anchor != null
            ? Quaternion.Inverse(itemGo.transform.rotation) * anchor.rotation
            : Quaternion.identity;

        if (hand == PlayerHand.Left)
        {
            LeftHandItemGO = itemGo;
            leftItemAnchorPosition = anchorPosition;
            leftItemAnchorRotation = anchorRotation;
        }
        else
        {
            RightHandItemGO = itemGo;
            rightItemAnchorPosition = anchorPosition;
            rightItemAnchorRotation = anchorRotation;
        }
        itemGo.SetActive(true);
    }

    private void HoldDesktopItem(GameObject itemGo, Vector3? targetPos, Quaternion? targetRot)
    {
        Multiplayer.LogDebug(() => $"NetworkedPlayer.HoldItem({itemGo.GetPath()}) Player: " +
            $"{username}, Target pos: {targetPos}, Target rot: {targetRot}");
        DropProjectedItem(itemGo);
        RetireDisplacedHandItem(PlayerHand.Right, itemGo);
        PrepareHeldItem(itemGo, disabledRHItemColliders);
        RightHandItemGO = itemGo;
        itemHoldPos = targetPos;
        itemHoldRot = targetRot;
    }

    private void PrepareHeldItem(GameObject itemGo, List<Collider> disabledColliders)
    {
        itemGo.transform.SetParent(selfTransform, true);
        GrabHandlerItem itemGrabHandler = itemGo.GetComponentInChildren<GrabHandlerItem>();
        if (itemGrabHandler != null)
        {
            itemGrabHandler.TogglePhysics(false);
            itemGrabHandler.interactionAllowed = false;
        }
        disabledColliders.Clear();
        foreach (Collider collider in itemGo.GetComponentsInChildren<Collider>(true))
        {
            if (collider != null && collider.enabled)
            {
                collider.enabled = false;
                disabledColliders.Add(collider);
            }
        }
    }

    private void ApplyVrHeldItemPositions()
    {
        ApplyVrHeldItem(LeftHandItemGO,
            selfTransform.position + targetRotation * currentLeftHandWorldPos,
            targetRotation * currentLeftHandWorldRot,
            leftItemAnchorPosition, leftItemAnchorRotation);
        ApplyVrHeldItem(RightHandItemGO,
            selfTransform.position + targetRotation * currentRightHandWorldPos,
            targetRotation * currentRightHandWorldRot,
            rightItemAnchorPosition, rightItemAnchorRotation);
    }

    private static void ApplyVrHeldItem(GameObject item, Vector3 handPosition,
        Quaternion handRotation, Vector3 anchorPosition, Quaternion anchorRotation)
    {
        if (item == null)
            return;
        Quaternion itemRotation = handRotation * Quaternion.Inverse(anchorRotation);
        item.transform.SetPositionAndRotation(
            handPosition - itemRotation * anchorPosition, itemRotation);
    }

    /// <summary>
    /// Moves every item projected onto this avatar back into the world so destroying the avatar
    /// does not destroy them. Held items go through the normal release path (colliders and
    /// physics restored); inventory items keep their hidden state. Their authority records stay
    /// keyed to the player's identity, so they are restored if the player rejoins.
    /// </summary>
    public void ReleaseAllItems()
    {
        DropProjectedItem(null);
        foreach (World.NetworkedItem item in GetComponentsInChildren<World.NetworkedItem>(true))
            if (item != null)
                item.transform.SetParent(WorldMover.OriginShiftParent, true);
    }

    private bool DropProjectedItem(GameObject expectedItem)
    {
        bool dropped = false;
        if (expectedItem == null || LeftHandItemGO == expectedItem)
            dropped |= DropVrHand(PlayerHand.Left);
        if (expectedItem == null || RightHandItemGO == expectedItem)
            dropped |= DropVrHand(PlayerHand.Right);
        return dropped;
    }

    private bool DropVrHand(PlayerHand hand)
    {
        GameObject held = hand == PlayerHand.Left ? LeftHandItemGO : RightHandItemGO;
        if (held == null)
            return false;
        List<Collider> colliders = hand == PlayerHand.Left
            ? disabledLHItemColliders : disabledRHItemColliders;
        ReleaseHeldItem(held, colliders);
        if (hand == PlayerHand.Left)
            LeftHandItemGO = null;
        else
        {
            RightHandItemGO = null;
            itemHoldPos = null;
            itemHoldRot = null;
        }
        return true;
    }

    private void RetireDisplacedHandItem(PlayerHand hand, GameObject replacement)
    {
        GameObject displaced = hand == PlayerHand.Left ? LeftHandItemGO : RightHandItemGO;
        if (displaced == null || displaced == replacement)
            return;

        // Selecting another item proves that the previous projection no longer occupies
        // this hand, but it does not prove that the previous item was dropped into the
        // world. Its own inventory/drop packet may arrive later or be lost. Parking it in
        // the remote inventory avoids leaving an active, illuminated tool attached to the
        // avatar or at the avatar's last position during that ordering gap.
        if (DropVrHand(hand))
        {
            AddItemToInventory(displaced);
            Multiplayer.LogDebug(() => $"NetworkedPlayer.RetireDisplacedHandItem({displaced.GetPath()}) " +
                $"Player: {username}, Hand: {hand}, Replacement: {replacement?.name}");
        }
    }

    private static void ReleaseHeldItem(GameObject heldItem, List<Collider> disabledColliders)
    {
        foreach (Collider collider in disabledColliders)
            if (collider != null)
                collider.enabled = true;
        disabledColliders.Clear();
        GrabHandlerItem itemGrabHandler = heldItem.GetComponentInChildren<GrabHandlerItem>();
        if (itemGrabHandler != null)
        {
            itemGrabHandler.TogglePhysics(true);
            itemGrabHandler.interactionAllowed = true;
        }
        heldItem.transform.SetParent(WorldMover.OriginShiftParent, true);
    }
}
