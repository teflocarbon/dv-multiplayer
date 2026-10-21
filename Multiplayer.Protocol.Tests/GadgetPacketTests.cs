using LiteNetLib.Utils;
using Multiplayer.Shared.Protocol.Data.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
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

    [Test]
    public void SettingRequest_RoundTripsItsKeysAndValues()
    {
        ServerboundGadgetSettingPacket result = null;
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        processor.SubscribeReusable<ServerboundGadgetSettingPacket>(packet => result = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundGadgetSettingPacket
        {
            ItemNetId = 801,
            OperationId = 42,
            Keys = new[] { "mode", "cut" },
            Values = new[] { 2f, 1f }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result.ItemNetId, Is.EqualTo(801));
        Assert.That(result.OperationId, Is.EqualTo(42));
        Assert.That(result.Keys, Is.EqualTo(new[] { "mode", "cut" }));
        Assert.That(result.Values, Is.EqualTo(new[] { 2f, 1f }));
    }

    [Test]
    public void State_RoundTripsWhoseRequestItAnswers()
    {
        ClientboundGadgetStatePacket result = null;
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        processor.SubscribeReusable<ClientboundGadgetStatePacket>(packet => result = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundGadgetStatePacket
        {
            ItemNetId = 801,
            Revision = 5,
            PlayerId = 3,
            OperationId = 42,
            Refused = true,
            Keys = new[] { "value" },
            Values = new[] { 0.75f }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result.ItemNetId, Is.EqualTo(801));
        Assert.That(result.Revision, Is.EqualTo(5));
        Assert.That(result.PlayerId, Is.EqualTo(3));
        Assert.That(result.OperationId, Is.EqualTo(42));
        Assert.That(result.Refused, Is.True);
        Assert.That(result.Keys, Is.EqualTo(new[] { "value" }));
        Assert.That(result.Values, Is.EqualTo(new[] { 0.75f }));
    }
}
