using System;
using Multiplayer.Core.Containers;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Containers;

[TestFixture]
public sealed class ColdContainerSnapshotCodecTests
{
    [Test]
    public void Snapshot_RoundTripsWithoutRuntimeHandles()
    {
        Guid containerId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        ColdContainerGraphSnapshot source = new()
        {
            Containers =
            {
                new ColdContainerRecord
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
                new ColdStoredItemRecord
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

        byte[] encoded = ColdContainerSnapshotCodec.Encode(source);
        bool decoded = ColdContainerSnapshotCodec.TryDecode(encoded, out var restored, out string reason);

        Assert.That(decoded, Is.True, reason);
        Assert.That(restored.Containers[0].SessionHandle, Is.Zero);
        Assert.That(restored.Containers[0].PrefabName, Is.EqualTo("Crate"));
        Assert.That(restored.Items[0].SessionHandle, Is.Zero);
        Assert.That(restored.Items[0].DetachedState, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(restored.Items[0].AuthoredItemKey, Is.EqualTo("0123456789abcdef0123456789abcdef"));
        ColdContainerGraph graph = new();
        Assert.That(graph.ImportSnapshot(restored).Accepted, Is.True);
    }

    [Test]
    public void Decode_RejectsTruncationAndTrailingBytes()
    {
        byte[] valid = ColdContainerSnapshotCodec.Encode(new ColdContainerGraphSnapshot());
        byte[] truncated = new byte[valid.Length - 1];
        Array.Copy(valid, truncated, truncated.Length);
        byte[] trailing = new byte[valid.Length + 1];
        Array.Copy(valid, trailing, valid.Length);

        Assert.That(ColdContainerSnapshotCodec.TryDecode(truncated, out _, out _), Is.False);
        Assert.That(ColdContainerSnapshotCodec.TryDecode(trailing, out _, out _), Is.False);
    }

    [Test]
    public void Encode_RejectsOversizedDetachedState()
    {
        ColdContainerGraphSnapshot snapshot = new();
        snapshot.Items.Add(new ColdStoredItemRecord
        {
            DetachedState = new byte[ColdContainerSnapshotCodec.MaximumDetachedStateBytes + 1]
        });

        Assert.Throws<System.IO.InvalidDataException>(() => ColdContainerSnapshotCodec.Encode(snapshot));
    }

    [Test]
    public void Export_NormalizesRuntimeCleanupStateToDurableColdState()
    {
        Guid containerId = Guid.NewGuid();
        ColdContainerGraph graph = new();
        graph.AddContainer(new ColdContainerRecord
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
            Item = new ColdStoredItemRecord
            {
                PersistentItemId = Guid.NewGuid(),
                PersistentOwnerIdentity = "owner",
                PrefabName = "Item"
            },
            PhysicalNetId = 7
        });

        ColdStoredItemRecord saved = graph.ExportSnapshot().Items[0];
        Assert.That(saved.LifecycleState, Is.EqualTo(ColdItemLifecycleState.Cold));
        Assert.That(saved.RetiringNetId, Is.Zero);
    }
}
