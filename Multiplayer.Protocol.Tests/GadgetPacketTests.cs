using LiteNetLib.Utils;
using Multiplayer.Shared.Protocol.Data.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using Multiplayer.Shared.Protocol.Serialization;
using NUnit.Framework;
using System.Linq;

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

    [Test]
    public void Changes_RoundTripTheirEntriesInOrder()
    {
        ClientboundGadgetChangesPacket result = null;
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        processor.SubscribeReusable<ClientboundGadgetChangesPacket>(packet => result = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundGadgetChangesPacket
        {
            CarNetId = 12,
            Entries = new[]
            {
                new GadgetChangeData
                    { Revision = 4, Kind = 1, ItemNetId = 801, Uid = 85, State = "{\"uid\":85}", PlayerId = 3, OperationId = 9 },
                new GadgetChangeData
                    { Revision = 5, Kind = 2, ItemNetId = 802, Uid = 86, Forced = true }
            }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result.CarNetId, Is.EqualTo(12));
        Assert.That(result.Entries.Select(e => e.Revision), Is.EqualTo(new uint[] { 4, 5 }));
        Assert.That(result.Entries[0].State, Is.EqualTo("{\"uid\":85}"));
        Assert.That((result.Entries[0].PlayerId, result.Entries[0].OperationId), Is.EqualTo(((byte)3, 9u)));
        Assert.That(result.Entries[1].Forced, Is.True);
        Assert.That(result.Entries[1].Kind, Is.EqualTo(2));
    }

    [Test]
    public void Install_RoundTripsItsPoseAndMount()
    {
        ServerboundGadgetInstallPacket result = null;
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        processor.SubscribeReusable<ServerboundGadgetInstallPacket>(packet => result = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundGadgetInstallPacket
        {
            OperationId = 7, ItemNetId = 801, CarNetId = 12, LocalPosition = new UnityEngine.Vector3(0.3f, 1.5f, -2f),
            LocalRotation = UnityEngine.Quaternion.identity, MountHostUid = 85, ColliderPath = "2/0/14", ColliderIndex = 1
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result.LocalPosition, Is.EqualTo(new UnityEngine.Vector3(0.3f, 1.5f, -2f)));
        Assert.That(result.MountHostUid, Is.EqualTo(85));
        Assert.That((result.ColliderPath, result.ColliderIndex), Is.EqualTo(("2/0/14", (byte)1)));
    }

    [Test]
    public void Command_RoundTrips()
    {
        ServerboundGadgetCommandPacket result = null;
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        processor.SubscribeReusable<ServerboundGadgetCommandPacket>(packet => result = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundGadgetCommandPacket { ItemNetId = 801, OperationId = 7, Command = 4 });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result.ItemNetId, Is.EqualTo(801));
        Assert.That(result.OperationId, Is.EqualTo(7));
        Assert.That(result.Command, Is.EqualTo(4));
    }

    [Test]
    public void Panel_RoundTrips()
    {
        ServerboundGadgetPanelPacket result = null;
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        processor.SubscribeReusable<ServerboundGadgetPanelPacket>(packet => result = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundGadgetPanelPacket { ItemNetId = 801, Input = 1, Control = 2, Value = -1f });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result.ItemNetId, Is.EqualTo(801));
        Assert.That(result.Input, Is.EqualTo(1));
        Assert.That(result.Control, Is.EqualTo(2));
        Assert.That(result.Value, Is.EqualTo(-1f));
    }

    [Test]
    public void Running_RoundTripsWhoseCommandItAnswers()
    {
        ClientboundGadgetRunningPacket result = null;
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        processor.SubscribeReusable<ClientboundGadgetRunningPacket>(packet => result = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundGadgetRunningPacket
        {
            ItemNetId = 801,
            PlayerId = 3,
            OperationId = 7,
            Refused = true,
            Keys = new[] { "countup", "completed" },
            Values = new[] { 12.5f, 0f }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result.ItemNetId, Is.EqualTo(801));
        Assert.That(result.PlayerId, Is.EqualTo(3));
        Assert.That(result.OperationId, Is.EqualTo(7));
        Assert.That(result.Refused, Is.True);
        Assert.That(result.Keys, Is.EqualTo(new[] { "countup", "completed" }));
        Assert.That(result.Values, Is.EqualTo(new[] { 12.5f, 0f }));
    }
}
