namespace Multiplayer.Core.Items;

public enum GrabberRuntimeState
{
    Unknown,
    Idle,
    Holding,
    Dragging,
    DraggingWhileHeld
}

public enum GrabberRecoveryAction
{
    None,
    ReleaseStaleHeldItem,
    ResetEmptyState
}

public readonly struct GrabberRecoveryInput
{
    public GrabberRecoveryInput(GrabberRuntimeState state, bool heldReferencePresent,
        bool heldItemReportsGrabbed, bool draggedReferencePresent)
    {
        State = state;
        HeldReferencePresent = heldReferencePresent;
        HeldItemReportsGrabbed = heldItemReportsGrabbed;
        DraggedReferencePresent = draggedReferencePresent;
    }

    public GrabberRuntimeState State { get; }
    public bool HeldReferencePresent { get; }
    public bool HeldItemReportsGrabbed { get; }
    public bool DraggedReferencePresent { get; }
}

public static class GrabberRecoveryPlanner
{
    public static GrabberRecoveryAction Plan(GrabberRecoveryInput input)
    {
        if (input.HeldReferencePresent && !input.HeldItemReportsGrabbed &&
            input.State is GrabberRuntimeState.Holding or GrabberRuntimeState.DraggingWhileHeld)
            return GrabberRecoveryAction.ReleaseStaleHeldItem;

        if (!input.HeldReferencePresent && !input.DraggedReferencePresent &&
            input.State is GrabberRuntimeState.Holding or GrabberRuntimeState.Dragging or
                GrabberRuntimeState.DraggingWhileHeld)
            return GrabberRecoveryAction.ResetEmptyState;

        return GrabberRecoveryAction.None;
    }
}
