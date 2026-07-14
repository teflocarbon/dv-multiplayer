namespace Multiplayer.Core.Items;

public enum WireItemState : byte
{
    Dropped,
    Thrown,
    InInventory,
    InHand,
    Attached,
    Removed
}

/// <summary>
/// Only fields that are meaningful for deciding whether an ItemState packet differs from the
/// last one sent. Transform and tracked-value changes travel through their own update paths.
/// </summary>
public readonly struct ItemWireStateProjection
{
    public ItemWireStateProjection(WireItemState state, byte placementPlayerId = 0,
        ushort attachedCarNetId = 0, bool attachedFront = false)
    {
        State = state;
        PlacementPlayerId = placementPlayerId;
        AttachedCarNetId = attachedCarNetId;
        AttachedFront = attachedFront;
    }

    public WireItemState State { get; }
    public byte PlacementPlayerId { get; }
    public ushort AttachedCarNetId { get; }
    public bool AttachedFront { get; }
}

public static class ItemWireStateComparer
{
    public static bool RequiresSend(bool hasPrevious, ItemWireStateProjection previous,
        ItemWireStateProjection current)
    {
        if (!hasPrevious || previous.State != current.State)
            return true;

        return current.State switch
        {
            WireItemState.InHand or WireItemState.InInventory =>
                previous.PlacementPlayerId != current.PlacementPlayerId,
            WireItemState.Attached =>
                previous.AttachedCarNetId != current.AttachedCarNetId ||
                previous.AttachedFront != current.AttachedFront,
            _ => false
        };
    }
}
