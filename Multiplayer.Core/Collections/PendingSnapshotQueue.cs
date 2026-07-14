using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Collections;

public enum PendingEnqueueStatus
{
    Accepted,
    DuplicateRevision,
    StaleRevision,
    CapacityExceeded,
    SupersededByFullSnapshot
}

public readonly struct PendingEnqueueResult
{
    public PendingEnqueueResult(PendingEnqueueStatus status, int supersededCount = 0)
    {
        Status = status;
        SupersededCount = supersededCount;
    }

    public PendingEnqueueStatus Status { get; }
    public int SupersededCount { get; }
    public bool Accepted => Status is PendingEnqueueStatus.Accepted or
        PendingEnqueueStatus.SupersededByFullSnapshot;
}

/// <summary>
/// Bounded FIFO for snapshots deferred behind a lifecycle dependency. Nonzero revisions are
/// monotonic. A full snapshot safely supersedes all older deltas; overflow is explicit and never
/// silently evicts a delta that may be required to reconstruct state.
/// </summary>
public sealed class PendingSnapshotQueue<T>
{
    private readonly Queue<T> queue = new();
    private readonly Func<T, uint> revisionSelector;
    private readonly Func<T, bool> fullSnapshotSelector;
    private uint highestRevision;
    private bool hasHighestRevision;

    public PendingSnapshotQueue(int capacity, Func<T, uint> revisionSelector,
        Func<T, bool> fullSnapshotSelector)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        this.revisionSelector = revisionSelector ?? throw new ArgumentNullException(nameof(revisionSelector));
        this.fullSnapshotSelector = fullSnapshotSelector ?? throw new ArgumentNullException(nameof(fullSnapshotSelector));
    }

    public int Capacity { get; }
    public int Count => queue.Count;

    public PendingEnqueueResult Enqueue(T value)
    {
        uint revision = revisionSelector(value);
        if (revision != 0 && hasHighestRevision)
        {
            if (revision == highestRevision)
                return new PendingEnqueueResult(PendingEnqueueStatus.DuplicateRevision);
            if (revision < highestRevision)
                return new PendingEnqueueResult(PendingEnqueueStatus.StaleRevision);
        }

        if (fullSnapshotSelector(value))
        {
            int superseded = queue.Count;
            queue.Clear();
            queue.Enqueue(value);
            RememberRevision(revision);
            return new PendingEnqueueResult(
                superseded > 0 ? PendingEnqueueStatus.SupersededByFullSnapshot : PendingEnqueueStatus.Accepted,
                superseded);
        }

        if (queue.Count >= Capacity)
            return new PendingEnqueueResult(PendingEnqueueStatus.CapacityExceeded);

        queue.Enqueue(value);
        RememberRevision(revision);
        return new PendingEnqueueResult(PendingEnqueueStatus.Accepted);
    }

    public bool TryDequeue(out T value)
    {
        if (queue.Count == 0)
        {
            value = default;
            return false;
        }
        value = queue.Dequeue();
        if (queue.Count == 0)
        {
            highestRevision = 0;
            hasHighestRevision = false;
        }
        return true;
    }

    public void Clear()
    {
        queue.Clear();
        highestRevision = 0;
        hasHighestRevision = false;
    }

    private void RememberRevision(uint revision)
    {
        if (revision == 0) return;
        highestRevision = revision;
        hasHighestRevision = true;
    }
}
