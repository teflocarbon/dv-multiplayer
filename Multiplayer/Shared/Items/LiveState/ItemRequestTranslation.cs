using System.Collections.Generic;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Items.LiveState;

/// <summary>
/// A player's own change to an item, as a request and back (docs/design/items/wire.md, "One
/// request, one result"). The requester's game sees the change as an update of its copy
/// (<see cref="NetworkedItem.GetSnapshot"/>) and sends it as a request (<see cref="FromObservation"/>);
/// the server turns the request back into that change (<see cref="ToChange"/>) for the validation
/// and the commit it has always used. Only what the action needs crosses the wire: the slot with
/// a stash or a slot move, the pose with a drop or a throw, the car with an attach, the tool and
/// its slot with a load into a magazine.
/// </summary>
public static class ItemRequestTranslation
{
    /// <summary>The request for a change the local game observed, or null if it isn't one a player may ask for.</summary>
    public static ItemRequest FromObservation(ItemUpdate snapshot)
    {
        if (snapshot == null || snapshot.ItemNetId == 0 ||
            (snapshot.UpdateType & (ItemUpdate.ItemUpdateType.Create | ItemUpdate.ItemUpdateType.Destroy)) != 0)
            return null;
        bool objectState = snapshot.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.ObjectState);
        ItemAction action = ItemUpdate.IncludesItemState(snapshot.UpdateType)
            ? snapshot.ItemState switch
            {
                ItemState.InHand => ItemAction.Take,
                ItemState.InInventory => ItemAction.Stash,
                ItemState.Dropped => ItemAction.Drop,
                ItemState.Thrown => ItemAction.Throw,
                ItemState.Attached => ItemAction.Attach,
                ItemState.InMagazine => ItemAction.LoadMagazine,
                _ => ItemAction.None
            }
            : objectState ? ItemAction.SetState : ItemAction.None;
        if (action == ItemAction.None)
            return null;
        bool stash = action == ItemAction.Stash;
        return new ItemRequest
        {
            ItemNetId = snapshot.ItemNetId,
            ExpectedRevision = snapshot.AuthorityRevision,
            Action = action,
            Hand = action == ItemAction.Take ? snapshot.PlayerHand : PlayerHand.None,
            Slot = stash ? snapshot.InventorySlot >= 0 ? snapshot.InventorySlot : snapshot.InventoryClaimSlot : -1,
            ClaimFlags = stash ? snapshot.InventoryClaimFlags : ItemInventoryClaimFlags.None,
            Position = snapshot.ItemPosition,
            Rotation = snapshot.ItemRotation,
            WorldParentKind = snapshot.WorldParentKind,
            WorldParentNetId = snapshot.WorldParentNetId,
            WorldParentKey = snapshot.WorldParentKey ?? string.Empty,
            ParentLocalPosition = snapshot.ParentLocalPosition,
            ParentLocalRotation = snapshot.ParentLocalRotation,
            ThrowDirection = snapshot.ThrowDirection,
            CarNetId = snapshot.CarNetId,
            Front = snapshot.AttachedFront,
            MagazineNetId = action == ItemAction.LoadMagazine ? snapshot.MagazineNetId : (ushort)0,
            MagazineSlot = action == ItemAction.LoadMagazine ? snapshot.MagazineSlot : -1,
            States = objectState ? snapshot.States ?? new Dictionary<string, object>() : null,
            WorldInteraction = snapshot.TransitionReason == ItemTransitionReason.ClientInteraction
        };
    }

    /// <summary>
    /// The change a move or state request asks for, as the server validates and commits it, made
    /// by <paramref name="playerId"/>; null for any other action.
    /// </summary>
    public static ItemUpdate ToChange(ItemRequest request, byte playerId)
    {
        if (request == null || request.ItemNetId == 0)
            return null;
        ItemState? state = request.Action switch
        {
            ItemAction.Take => ItemState.InHand,
            ItemAction.Stash or ItemAction.MoveSlot => ItemState.InInventory,
            ItemAction.Drop => ItemState.Dropped,
            ItemAction.Throw => ItemState.Thrown,
            ItemAction.Attach => ItemState.Attached,
            ItemAction.LoadMagazine => ItemState.InMagazine,
            _ => null
        };
        if (state == null && request.Action != ItemAction.SetState)
            return null;
        bool pose = ItemRequest.CarriesPose(request.Action);
        ItemUpdate.ItemUpdateType type = state != null ? ItemUpdate.ItemUpdateType.ItemState : ItemUpdate.ItemUpdateType.None;
        if (request.Action == ItemAction.SetState || request.States != null)
            type |= ItemUpdate.ItemUpdateType.ObjectState;
        return new ItemUpdate
        {
            UpdateType = type,
            ItemNetId = request.ItemNetId,
            AuthorityRevision = request.ExpectedRevision,
            ItemState = state ?? ItemState.Dropped,
            PlayerId = playerId,
            PlayerHand = request.Action == ItemAction.Take ? request.Hand : PlayerHand.None,
            // The slot it goes in; its claim is the ledger's.
            InventorySlot = request.Action is ItemAction.Stash or ItemAction.MoveSlot ? request.Slot : -1,
            InventoryClaimSlot = -1,
            InventoryClaimFlags = request.Action == ItemAction.Stash ? request.ClaimFlags : ItemInventoryClaimFlags.None,
            ItemPosition = pose ? request.Position : default,
            ItemRotation = pose ? request.Rotation : UnityEngine.Quaternion.identity,
            WorldParentKind = pose ? request.WorldParentKind : ItemWorldParentKind.World,
            WorldParentNetId = pose ? request.WorldParentNetId : (ushort)0,
            WorldParentKey = pose ? request.WorldParentKey ?? string.Empty : string.Empty,
            ParentLocalPosition = pose ? request.ParentLocalPosition : default,
            ParentLocalRotation = pose ? request.ParentLocalRotation : UnityEngine.Quaternion.identity,
            ThrowDirection = request.Action == ItemAction.Throw ? request.ThrowDirection : default,
            CarNetId = request.Action == ItemAction.Attach ? request.CarNetId : (ushort)0,
            AttachedFront = request.Action != ItemAction.Attach || request.Front,
            MagazineNetId = request.Action == ItemAction.LoadMagazine ? request.MagazineNetId : (ushort)0,
            MagazineSlot = request.Action == ItemAction.LoadMagazine ? request.MagazineSlot : -1,
            States = request.States,
            TransitionReason = request.WorldInteraction ? ItemTransitionReason.ClientInteraction : ItemTransitionReason.ClientState,
            OriginatingPlayerId = playerId
        };
    }
}
