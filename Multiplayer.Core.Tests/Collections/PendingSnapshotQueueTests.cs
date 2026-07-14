using Multiplayer.Core.Collections;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Collections;

[TestFixture]
public sealed class PendingSnapshotQueueTests
{
    [Test]
    public void Queue_DrainsInFifoOrder()
    {
        PendingSnapshotQueue<Snapshot> queue = Queue(capacity: 3);
        queue.Enqueue(new Snapshot(1));
        queue.Enqueue(new Snapshot(2));
        queue.Enqueue(new Snapshot(3));

        Assert.That(queue.TryDequeue(out Snapshot first), Is.True);
        Assert.That(queue.TryDequeue(out Snapshot second), Is.True);
        Assert.That(queue.TryDequeue(out Snapshot third), Is.True);
        Assert.That(new[] { first.Revision, second.Revision, third.Revision },
            Is.EqualTo(new uint[] { 1, 2, 3 }));
        Assert.That(queue.TryDequeue(out _), Is.False);
    }

    [Test]
    public void DuplicateRevision_IsRejectedWithoutGrowingQueue()
    {
        PendingSnapshotQueue<Snapshot> queue = Queue();
        Assert.That(queue.Enqueue(new Snapshot(4)).Accepted, Is.True);

        PendingEnqueueResult duplicate = queue.Enqueue(new Snapshot(4));

        Assert.That(duplicate.Status, Is.EqualTo(PendingEnqueueStatus.DuplicateRevision));
        Assert.That(queue.Count, Is.EqualTo(1));
    }

    [Test]
    public void OlderRevision_IsRejectedWithoutGrowingQueue()
    {
        PendingSnapshotQueue<Snapshot> queue = Queue();
        queue.Enqueue(new Snapshot(7));

        PendingEnqueueResult stale = queue.Enqueue(new Snapshot(6));

        Assert.That(stale.Status, Is.EqualTo(PendingEnqueueStatus.StaleRevision));
        Assert.That(queue.Count, Is.EqualTo(1));
    }

    [Test]
    public void FullSnapshot_SupersedesAllOlderDeltas()
    {
        PendingSnapshotQueue<Snapshot> queue = Queue();
        queue.Enqueue(new Snapshot(1));
        queue.Enqueue(new Snapshot(2));
        queue.Enqueue(new Snapshot(3));

        PendingEnqueueResult result = queue.Enqueue(new Snapshot(4, Full: true));

        Assert.That(result.Status, Is.EqualTo(PendingEnqueueStatus.SupersededByFullSnapshot));
        Assert.That(result.SupersededCount, Is.EqualTo(3));
        Assert.That(queue.Count, Is.EqualTo(1));
        Assert.That(queue.TryDequeue(out Snapshot remaining), Is.True);
        Assert.That(remaining.Revision, Is.EqualTo(4));
        Assert.That(remaining.Full, Is.True);
    }

    [Test]
    public void CapacityExceeded_IsExplicitAndDoesNotEvictRequiredDelta()
    {
        PendingSnapshotQueue<Snapshot> queue = Queue(capacity: 2);
        queue.Enqueue(new Snapshot(1));
        queue.Enqueue(new Snapshot(2));

        PendingEnqueueResult overflow = queue.Enqueue(new Snapshot(3));

        Assert.That(overflow.Status, Is.EqualTo(PendingEnqueueStatus.CapacityExceeded));
        Assert.That(queue.Count, Is.EqualTo(2));
        queue.TryDequeue(out Snapshot first);
        Assert.That(first.Revision, Is.EqualTo(1));
    }

    [Test]
    public void FullSnapshot_CanRecoverAFullQueue()
    {
        PendingSnapshotQueue<Snapshot> queue = Queue(capacity: 2);
        queue.Enqueue(new Snapshot(1));
        queue.Enqueue(new Snapshot(2));

        PendingEnqueueResult recovery = queue.Enqueue(new Snapshot(3, Full: true));

        Assert.That(recovery.Accepted, Is.True);
        Assert.That(recovery.SupersededCount, Is.EqualTo(2));
        Assert.That(queue.Count, Is.EqualTo(1));
    }

    [Test]
    public void LegacyZeroRevisions_RemainOrderedAndAreNotMistakenForDuplicates()
    {
        PendingSnapshotQueue<Snapshot> queue = Queue();
        Assert.That(queue.Enqueue(new Snapshot(0)).Accepted, Is.True);
        Assert.That(queue.Enqueue(new Snapshot(0)).Accepted, Is.True);
        Assert.That(queue.Count, Is.EqualTo(2));
    }

    [Test]
    public void EmptyDrain_ResetsRevisionBaseline()
    {
        PendingSnapshotQueue<Snapshot> queue = Queue();
        queue.Enqueue(new Snapshot(10));
        queue.TryDequeue(out _);

        Assert.That(queue.Enqueue(new Snapshot(1)).Accepted, Is.True);
    }

    [Test]
    public void Clear_ResetsCountAndRevisionBaseline()
    {
        PendingSnapshotQueue<Snapshot> queue = Queue();
        queue.Enqueue(new Snapshot(10));
        queue.Clear();

        Assert.That(queue.Count, Is.Zero);
        Assert.That(queue.Enqueue(new Snapshot(1)).Accepted, Is.True);
    }

    private static PendingSnapshotQueue<Snapshot> Queue(int capacity = 8) =>
        new(capacity, value => value.Revision, value => value.Full);

    private sealed record Snapshot(uint Revision, bool Full = false);
}
