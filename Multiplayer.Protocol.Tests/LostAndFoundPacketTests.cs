using LiteNetLib.Utils;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Serverbound;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class LostAndFoundPacketTests
{
    [Test]
    public void Snapshot_RoundTripsAllListFields()
    {
        ClientboundLostItemsSnapshotPacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundLostItemsSnapshotPacket>(packet => result = new ClientboundLostItemsSnapshotPacket
        {
            RequestId = packet.RequestId,
            Generation = packet.Generation,
            NetIds = packet.NetIds,
            Revisions = packet.Revisions,
            PrefabNames = packet.PrefabNames,
            DisplayNames = packet.DisplayNames,
            Reasons = packet.Reasons,
            LostUtcTicks = packet.LostUtcTicks
        });
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundLostItemsSnapshotPacket
        {
            RequestId = 11,
            Generation = 4,
            NetIds = new ushort[] { 782 },
            Revisions = new uint[] { 9 },
            PrefabNames = new[] { "CommsRadio" },
            DisplayNames = new[] { "Radio" },
            Reasons = new byte[] { 2 },
            LostUtcTicks = new long[] { 638880000000000000 }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.RequestId, Is.EqualTo(11));
        Assert.That(result.NetIds[0], Is.EqualTo(782));
        Assert.That(result.LostUtcTicks[0], Is.EqualTo(638880000000000000));
    }

    [Test]
    public void RetrievalRequest_RoundTripsInventoryEvidence()
    {
        ServerboundLostItemRetrievePacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ServerboundLostItemRetrievePacket>(packet => result = new ServerboundLostItemRetrievePacket
        {
            RequestId = packet.RequestId,
            ItemNetId = packet.ItemNetId,
            ExpectedRevision = packet.ExpectedRevision,
            RequestedSlot = packet.RequestedSlot,
            ExistingItemSlot = packet.ExistingItemSlot,
            InventoryCapacity = packet.InventoryCapacity,
            OccupiedSlots = packet.OccupiedSlots
        });
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundLostItemRetrievePacket
        {
            RequestId = 12,
            ItemNetId = 782,
            ExpectedRevision = 9,
            RequestedSlot = 3,
            ExistingItemSlot = 5,
            InventoryCapacity = 12,
            OccupiedSlots = new[] { 0, 1, 2 }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Assert.That(result.ItemNetId, Is.EqualTo(782));
        Assert.That(result.ExistingItemSlot, Is.EqualTo(5));
        Assert.That(result.OccupiedSlots, Is.EqualTo(new[] { 0, 1, 2 }));
    }
}
