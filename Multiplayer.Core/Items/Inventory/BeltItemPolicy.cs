using Multiplayer.Core.Items.Projection;

namespace Multiplayer.Core.Items.Inventory;

/// <summary>
/// Rules for items on a VR player's belt. Derail Valley's belt slots (inventory slots 33-35 in VR)
/// are ordinary inventory slots whose item stays active and is snapped, visible, to a belt snap
/// point on the player's body. To multiplayer such an item is in its owner's inventory: the snap
/// is only how the owner's game shows that slot.
/// </summary>
public static class BeltItemPolicy
{
    /// <summary>
    /// Whether to keep reporting the last state for an item the local VR player let go of this
    /// frame. The belt takes a released item only at the end of that frame
    /// (ItemSnapPointInteractionBelt.OnUngrabDelayed), so until then the item looks dropped. A
    /// Dropped sent in that gap gave the item to the world and the inventory update behind it was
    /// refused or arrived out of turn, and the item fell off the belt. Nothing at all is sent for
    /// that frame: letting go clears the item's inventory reservation, and an InHand sent for that
    /// change came back after the item was on the belt (<see cref="IsSupersededOwnHold"/>).
    /// </summary>
    public static bool HoldVrRelease(bool beltSnappable, int releaseFrame, int currentFrame,
        bool grabbed, bool inLocalInventory) =>
        beltSnappable && releaseFrame >= 0 && releaseFrame == currentFrame &&
        !grabbed && !inLocalInventory;

    /// <summary>
    /// The host's echo of our own InHand, arriving after we let go and the item went into our
    /// inventory (a VR belt clip, or a quick stash). The InInventory we sent since follows it.
    /// Applied, it unsnapped a belt item: it fell to the floor while staying in its inventory slot,
    /// and DV refuses to grab an item it thinks is in the inventory, so it could never be picked up.
    /// Host corrections carry no originating player, so they are never mistaken for an echo.
    /// </summary>
    public static bool IsSupersededOwnHold(bool isHost, byte localPlayerId, byte originatingPlayerId,
        WireItemState receivedState, bool grabbed, bool inLocalInventory) =>
        !isHost && localPlayerId != 0 && originatingPlayerId == localPlayerId &&
        receivedState == WireItemState.InHand && !grabbed && inLocalInventory;

    /// <summary>
    /// Whether applying a snapshot must leave the item snapped where it is. Placement normally
    /// unsnaps an item first, but an item on the local player's belt that the snapshot puts in the
    /// local player's inventory is already where it belongs: unsnapping it dropped it to the floor
    /// while it stayed in the inventory slot.
    /// </summary>
    public static bool KeepBeltSnap(WireItemState targetState, byte targetPlayerId,
        byte localPlayerId, bool snappedToBelt, bool inLocalInventory) =>
        snappedToBelt && inLocalInventory && targetState == WireItemState.InInventory &&
        localPlayerId != 0 && targetPlayerId == localPlayerId;
}
