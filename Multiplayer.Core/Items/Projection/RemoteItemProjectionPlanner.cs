namespace Multiplayer.Core.Items.Projection;

public enum RemoteItemProjectionAction
{
    LocalHand,
    LocalInventory,
    RemoteHand,
    RemoteInventory,
    MissingRemotePlayer,
    InvalidState
}

public readonly struct RemoteItemProjectionPlan
{
    public RemoteItemProjectionPlan(RemoteItemProjectionAction action,
        bool clearExistingRemoteHands, bool activate, bool deactivate)
    {
        Action = action;
        ClearExistingRemoteHands = clearExistingRemoteHands;
        Activate = activate;
        Deactivate = deactivate;
    }

    public RemoteItemProjectionAction Action { get; }
    public bool ClearExistingRemoteHands { get; }
    public bool Activate { get; }
    public bool Deactivate { get; }
}

public static class RemoteItemProjectionPlanner
{
    public static RemoteItemProjectionPlan Plan(WireItemState state, byte targetPlayerId,
        byte localPlayerId, bool targetRemotePlayerExists)
    {
        if (state is not (WireItemState.InHand or WireItemState.InInventory) || targetPlayerId == 0)
            return new RemoteItemProjectionPlan(RemoteItemProjectionAction.InvalidState,
                false, false, false);

        if (targetPlayerId == localPlayerId && localPlayerId != 0)
        {
            return new RemoteItemProjectionPlan(
                state == WireItemState.InHand
                    ? RemoteItemProjectionAction.LocalHand
                    : RemoteItemProjectionAction.LocalInventory,
                clearExistingRemoteHands: true,
                activate: state == WireItemState.InHand,
                deactivate: false);
        }

        if (!targetRemotePlayerExists)
            return new RemoteItemProjectionPlan(RemoteItemProjectionAction.MissingRemotePlayer,
                true, false, true);

        return new RemoteItemProjectionPlan(
            state == WireItemState.InHand
                ? RemoteItemProjectionAction.RemoteHand
                : RemoteItemProjectionAction.RemoteInventory,
            // Player placement snapshots are canonical, not incremental. Clear every
            // existing projection of this item before assigning its one destination;
            // cached holder bookkeeping can be stale after interest retirement,
            // recreation, a hand switch, or an interrupted inventory transition.
            clearExistingRemoteHands: true,
            activate: state == WireItemState.InHand,
            deactivate: state == WireItemState.InInventory);
    }
}
