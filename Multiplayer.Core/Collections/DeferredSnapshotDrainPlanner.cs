namespace Multiplayer.Core.Collections;

public enum DeferredSnapshotDrainAction
{
    ApplyFullSnapshot,
    ApplyTrackedStateOnly,
    SkipStaleSnapshot
}

public readonly struct DeferredSnapshotDrainPlan
{
    public DeferredSnapshotDrainPlan(DeferredSnapshotDrainAction action, string reason)
    {
        Action = action;
        Reason = reason;
    }

    public DeferredSnapshotDrainAction Action { get; }
    public string Reason { get; }
}

/// <summary>
/// Prevents a snapshot queued behind a lifecycle dependency from rolling authority or
/// placement backwards after a newer snapshot has already been applied. Older tracked state
/// can still be merged because it may be the Create snapshot which registered that state.
/// </summary>
public static class DeferredSnapshotDrainPlanner
{
    public static DeferredSnapshotDrainPlan Plan(uint pendingRevision, uint currentRevision,
        bool hasTrackedState)
    {
        if (currentRevision == 0 || pendingRevision >= currentRevision)
            return new(DeferredSnapshotDrainAction.ApplyFullSnapshot, "current-or-legacy-snapshot");

        return hasTrackedState
            ? new(DeferredSnapshotDrainAction.ApplyTrackedStateOnly,
                "stale-placement-preserve-tracked-state")
            : new(DeferredSnapshotDrainAction.SkipStaleSnapshot,
                "stale-snapshot-without-tracked-state");
    }
}
