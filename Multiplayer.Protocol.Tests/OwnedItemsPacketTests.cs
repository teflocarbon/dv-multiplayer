using LiteNetLib.Utils;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

/// <summary>The owned-items list's request and rows (owned-items.md, PR 6a).</summary>
[TestFixture]
public sealed class OwnedItemsPacketTests
{
    [Test]
    public void List_RoundTripsEveryRow()
    {
        ClientboundOwnedItemsPacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundOwnedItemsPacket>(packet => result = new ClientboundOwnedItemsPacket
        {
            RequestId = packet.RequestId,
            Handles = packet.Handles,
            PrefabNames = packet.PrefabNames,
            DisplayNames = packet.DisplayNames,
            Wheres = packet.Wheres,
            Retrievable = packet.Retrievable
        });
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundOwnedItemsPacket
        {
            RequestId = 7,
            Handles = new uint[] { 1, 2 },
            PrefabNames = new[] { "CommsRadio", "Lantern" },
            DisplayNames = new[] { "Radio", "Lantern" },
            Wheres = new[] { "in your inventory", "in L-042" },
            Retrievable = new[] { false, true }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result, Is.Not.Null);
        Assert.That(result.RequestId, Is.EqualTo(7));
        Assert.That(result.Handles, Is.EqualTo(new uint[] { 1, 2 }));
        Assert.That(result.Wheres[1], Is.EqualTo("in L-042"));
        Assert.That(result.Retrievable, Is.EqualTo(new[] { false, true }));
    }

    [Test]
    public void Request_RoundTripsItsId()
    {
        uint got = 0;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ServerboundOwnedItemsRequestPacket>(packet => got = packet.RequestId);
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundOwnedItemsRequestPacket { RequestId = 42 });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(got, Is.EqualTo(42));
    }
}
