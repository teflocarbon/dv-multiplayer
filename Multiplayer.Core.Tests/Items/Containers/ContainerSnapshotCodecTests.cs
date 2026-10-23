using System;
using Multiplayer.Core.Items.Containers;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Containers;

[TestFixture]
public sealed class ContainerSnapshotCodecTests
{
    [Test]
    public void Snapshot_RoundTripsWithoutRuntimeHandles()
    {
        Guid containerId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        ContainerGraphSnapshot source = new()
        {
            Containers =
            {
                new ContainerRecord
                {
                    PersistentContainerId = containerId,
                    OwnerIdentity = "owner",
                    PrefabName = "Crate",
                    Capacity = 24,
                    Revision = 12,
                    SessionHandle = 88
                }
            },
            Items =
            {
                new StoredItemRecord
                {
                    PersistentItemId = itemId,
                    ParentContainerId = containerId,
                    PersistentOwnerIdentity = "owner",
                    AuthoredItemKey = "0123456789abcdef0123456789abcdef",
                    PrefabName = "Folder",
                    DisplayName = "Folder",
                    Slot = 4,
                    StateVersion = 3,
                    DetachedState = new byte[] { 1, 2, 3 },
                    SessionHandle = 99
                }
            }
        };

        byte[] encoded = ContainerSnapshotCodec.Encode(source);
        bool decoded = ContainerSnapshotCodec.TryDecode(encoded, out var restored, out string reason);

        Assert.That(decoded, Is.True, reason);
        Assert.That(restored.Containers[0].SessionHandle, Is.Zero);
        Assert.That(restored.Containers[0].PrefabName, Is.EqualTo("Crate"));
        Assert.That(restored.Items[0].SessionHandle, Is.Zero);
        Assert.That(restored.Items[0].DetachedState, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(restored.Items[0].AuthoredItemKey, Is.EqualTo("0123456789abcdef0123456789abcdef"));
        ContainerGraph graph = new();
        Assert.That(graph.ImportSnapshot(restored).Accepted, Is.True);
    }

    [Test]
    public void Decode_RejectsTruncationAndTrailingBytes()
    {
        byte[] valid = ContainerSnapshotCodec.Encode(new ContainerGraphSnapshot());
        byte[] truncated = new byte[valid.Length - 1];
        Array.Copy(valid, truncated, truncated.Length);
        byte[] trailing = new byte[valid.Length + 1];
        Array.Copy(valid, trailing, valid.Length);

        Assert.That(ContainerSnapshotCodec.TryDecode(truncated, out _, out _), Is.False);
        Assert.That(ContainerSnapshotCodec.TryDecode(trailing, out _, out _), Is.False);
    }

    [Test]
    public void Encode_RejectsOversizedDetachedState()
    {
        ContainerGraphSnapshot snapshot = new();
        snapshot.Items.Add(new StoredItemRecord
        {
            DetachedState = new byte[ContainerSnapshotCodec.MaximumDetachedStateBytes + 1]
        });

        Assert.Throws<System.IO.InvalidDataException>(() => ContainerSnapshotCodec.Encode(snapshot));
    }

    [Test]
    public void Export_NormalizesRuntimeCleanupStateToDurableColdState()
    {
        Guid containerId = Guid.NewGuid();
        ContainerGraph graph = new();
        graph.AddContainer(new ContainerRecord
        {
            PersistentContainerId = containerId,
            OwnerIdentity = "owner",
            Capacity = 1
        });
        graph.CommitDeposit(new DepositCommand
        {
            OperationId = Guid.NewGuid(),
            ActorIdentity = "owner",
            DestinationContainerId = containerId,
            Item = new StoredItemRecord
            {
                PersistentItemId = Guid.NewGuid(),
                PersistentOwnerIdentity = "owner",
                PrefabName = "Item"
            },
            PhysicalNetId = 7
        });

        StoredItemRecord saved = graph.ExportSnapshot().Items[0];
        Assert.That(saved.LifecycleState, Is.EqualTo(ItemLifecycleState.Cold));
        Assert.That(saved.RetiringNetId, Is.Zero);
    }
}
