using System.Collections.Generic;
using Multiplayer.Core.Identity;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Identity;

[TestFixture]
public sealed class NetIdAllocatorTests
{
    [Test]
    public void AllocationStartsAtOneAndNeverReturnsZero()
    {
        NetIdAllocator<ushort> allocator = UShortAllocator();
        Assert.That(allocator.TryAllocate(out ushort first), Is.True);
        Assert.That(first, Is.EqualTo(1));
        Assert.That(first, Is.Not.Zero);
    }

    [Test]
    public void LiveIdCannotBeReservedOrAllocatedTwice()
    {
        NetIdAllocator<ushort> allocator = UShortAllocator();
        allocator.TryReserve(1);
        Assert.That(allocator.TryReserve(1), Is.False);
        allocator.TryAllocate(out ushort next);
        Assert.That(next, Is.EqualTo(2));
        Assert.That(allocator.LiveCount, Is.EqualTo(2));
    }

    [Test]
    public void ReleasedIdIsReusedOnce()
    {
        NetIdAllocator<ushort> allocator = UShortAllocator();
        allocator.TryAllocate(out ushort first);
        Assert.That(allocator.Release(first), Is.True);
        Assert.That(allocator.Release(first), Is.False);
        allocator.TryAllocate(out ushort reused);
        allocator.TryAllocate(out ushort next);
        Assert.That(reused, Is.EqualTo(first));
        Assert.That(next, Is.EqualTo(2));
    }

    [Test]
    public void ExplicitReservationIsSkippedBySequentialAllocation()
    {
        NetIdAllocator<ushort> allocator = UShortAllocator();
        allocator.TryReserve(2);
        allocator.TryAllocate(out ushort first);
        allocator.TryAllocate(out ushort second);
        Assert.That(first, Is.EqualTo(1));
        Assert.That(second, Is.EqualTo(3));
    }

    [Test]
    public void ByteAllocatorReportsExhaustionWithoutWrappingToZero()
    {
        NetIdAllocator<byte> allocator = new(value => unchecked((byte)(value + 1)));
        for (int expected = 1; expected <= byte.MaxValue; expected++)
        {
            Assert.That(allocator.TryAllocate(out byte id), Is.True);
            Assert.That(id, Is.EqualTo((byte)expected));
        }
        Assert.That(allocator.TryAllocate(out byte exhausted), Is.False);
        Assert.That(exhausted, Is.Zero);
    }

    [Test]
    public void ExhaustedAllocatorCanRecoverReleasedId()
    {
        NetIdAllocator<byte> allocator = new(value => unchecked((byte)(value + 1)));
        for (int i = 0; i < byte.MaxValue; i++) allocator.TryAllocate(out _);
        allocator.Release(100);
        Assert.That(allocator.TryAllocate(out byte recovered), Is.True);
        Assert.That(recovered, Is.EqualTo(100));
    }

    [Test]
    public void ResetClearsLiveAndReleasedState()
    {
        NetIdAllocator<ushort> allocator = UShortAllocator();
        allocator.TryAllocate(out ushort id);
        allocator.Release(id);
        allocator.Reset();
        Assert.That(allocator.LiveCount, Is.Zero);
        allocator.TryAllocate(out ushort restarted);
        Assert.That(restarted, Is.EqualTo(1));
    }

    [Test]
    public void LongAllocateReleaseSequenceNeverProducesDuplicateLiveIds()
    {
        NetIdAllocator<ushort> allocator = UShortAllocator();
        var live = new HashSet<ushort>();
        for (int cycle = 0; cycle < 10000; cycle++)
        {
            Assert.That(allocator.TryAllocate(out ushort id), Is.True);
            Assert.That(id, Is.Not.Zero);
            Assert.That(live.Add(id), Is.True, $"duplicate live ID at cycle {cycle}");
            if (cycle % 3 == 0)
            {
                Assert.That(allocator.Release(id), Is.True);
                live.Remove(id);
            }
        }
        Assert.That(allocator.LiveCount, Is.EqualTo(live.Count));
    }

    private static NetIdAllocator<ushort> UShortAllocator() =>
        new(value => unchecked((ushort)(value + 1)));
}
