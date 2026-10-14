using System;
using LiteNetLib.Utils;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Serverbound;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class ColdContainerPacketTests
{
    [Test]
    public void BrowseRequest_UsesCompactRuntimeIdentity()
    {
        ServerboundContainerBrowsePacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ServerboundContainerBrowsePacket>(packet => result = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundContainerBrowsePacket
        {
            RequestId = 7, ShellNetId = 800, ContainerHandle = 0, Offset = 16, Count = 24
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Assert.That(result.ShellNetId, Is.EqualTo(800));
        Assert.That(result.Count, Is.EqualTo(24));
    }

    [Test]
    public void View_RoundTripsDetachedRowsWithoutPersistentUuids()
    {
        ClientboundContainerViewPacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundContainerViewPacket>(packet => result = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundContainerViewPacket
        {
            RequestId = 8, Accepted = true, RejectionReason = string.Empty,
            ContainerHandle = 41, Revision = 3, Capacity = 24,
            Slots = new[] { 2 }, ItemHandles = new uint[] { 42 },
            PrefabNames = new[] { "Folder" }, DisplayNames = new[] { "Folder" },
            ForeignOwned = new[] { false }, ChildContainerHandles = new uint[] { 43 },
            ChildItemCounts = new[] { 6 }, StateVersions = new uint[] { 2 }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Assert.That(result.ContainerHandle, Is.EqualTo(41));
        Assert.That(result.ItemHandles[0], Is.EqualTo(42));
        Assert.That(result.ChildContainerHandles[0], Is.EqualTo(43));
    }

    [Test]
    public void Mutation_RoundTripsIdempotencyAndRevisions()
    {
        byte[] operation = Guid.NewGuid().ToByteArray();
        ServerboundContainerMutationPacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ServerboundContainerMutationPacket>(packet => result = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundContainerMutationPacket
        {
            RequestId = 9, OperationId = operation, Kind = 2,
            SourceContainerHandle = 5, ExpectedSourceRevision = 11, SourceSlot = 3,
            DestinationSlot = 17
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Assert.That(result.OperationId, Is.EqualTo(operation));
        Assert.That(result.ExpectedSourceRevision, Is.EqualTo(11));
        Assert.That(result.DestinationSlot, Is.EqualTo(17),
            "Withdrawal must retain the concrete inventory projection slot.");
    }

    [Test]
    public void MutationResult_RoundTripsRejectionAndMaterializedNetId()
    {
        byte[] operation = Guid.NewGuid().ToByteArray();
        ClientboundContainerMutationResultPacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundContainerMutationResultPacket>(packet => result = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundContainerMutationResultPacket
        {
            RequestId = 10,
            OperationId = operation,
            Kind = 2,
            Accepted = false,
            Status = 3,
            RejectionReason = "container-revision-stale",
            SourceRevision = 14,
            DestinationRevision = 15,
            MaterializedItemNetId = 812
        });

        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result.OperationId, Is.EqualTo(operation));
        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectionReason, Is.EqualTo("container-revision-stale"));
        Assert.That(result.MaterializedItemNetId, Is.EqualTo(812));
    }
}
