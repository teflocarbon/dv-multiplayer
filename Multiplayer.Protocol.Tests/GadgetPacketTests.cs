using LiteNetLib.Utils;
using Multiplayer.Shared.Protocol.Data.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Serialization;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class GadgetPacketTests
{
    [Test]
    public void JoinSnapshot_RoundTripsEveryCarsGadgets()
    {
        ClientboundGadgetJoinSnapshotPacket result = null;
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        processor.SubscribeReusable<ClientboundGadgetJoinSnapshotPacket>(packet => result = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundGadgetJoinSnapshotPacket
        {
            Anchors = new[]
            {
                new GadgetAnchorSnapshotData
                {
                    CarNetId = 12,
                    Gadgets = new[]
                    {
                        new InstalledGadgetData { ItemNetId = 801, Uid = 7, State = "{\"placedOn\":\"guid-a\",\"gadgetData\":{\"uid\":7}}" },
                        new InstalledGadgetData { ItemNetId = 802, Uid = 9, State = "{}" }
                    }
                },
                new GadgetAnchorSnapshotData { CarNetId = 13, Gadgets = new InstalledGadgetData[0] }
            }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result.Anchors, Has.Length.EqualTo(2));
        Assert.That(result.Anchors[0].CarNetId, Is.EqualTo(12));
        Assert.That(result.Anchors[0].Gadgets[1].ItemNetId, Is.EqualTo(802));
        Assert.That(result.Anchors[0].Gadgets[0].Uid, Is.EqualTo(7));
        Assert.That(result.Anchors[0].Gadgets[0].State, Does.Contain("guid-a"));
        Assert.That(result.Anchors[1].Gadgets, Is.Empty);
    }

    [Test]
    public void JoinSnapshot_WithNoGadgetsStillArrives()
    {
        ClientboundGadgetJoinSnapshotPacket result = null;
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        processor.SubscribeReusable<ClientboundGadgetJoinSnapshotPacket>(packet => result = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundGadgetJoinSnapshotPacket());
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Anchors, Is.Empty);
    }
}
