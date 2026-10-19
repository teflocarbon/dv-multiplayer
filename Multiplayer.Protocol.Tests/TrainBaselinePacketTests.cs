using DV.LocoRestoration;
using LiteNetLib.Utils;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Protocol.Serialization;
using NUnit.Framework;
using UnityEngine;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class TrainBaselinePacketTests
{
    [Test]
    public void SpawnTrainsetRoundTripPreservesEveryArrayElement()
    {
        ClientboundSpawnTrainSetPacket received = null;
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        processor.SubscribeReusable<ClientboundSpawnTrainSetPacket>(packet => received = packet);

        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundSpawnTrainSetPacket
        {
            BaselineId = 74,
            Sequence = 3,
            AutoCouple = true,
            SpawnParts = new[] { Part(27, "L-070"), Part(33, "CFF445") }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(received, Is.Not.Null);
        Assert.That(received.AutoCouple, Is.True);
        Assert.That(received.BaselineId, Is.EqualTo(74));
        Assert.That(received.Sequence, Is.EqualTo(3));
        Assert.That(received.SpawnParts, Has.Length.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(received.SpawnParts[0].NetId, Is.EqualTo(27));
            Assert.That(received.SpawnParts[0].CarId, Is.EqualTo("L-070"));
            Assert.That(received.SpawnParts[0].LiveryId, Is.EqualTo("LocoDE2"));
            Assert.That(received.SpawnParts[1].NetId, Is.EqualTo(33));
            Assert.That(received.SpawnParts[1].CarId, Is.EqualTo("CFF445"));
            Assert.That(received.SpawnParts[1].LiveryId, Is.EqualTo("Flatbed"));
        });
    }

    [Test]
    public void BeginAndCompleteRoundTripTheSameManifest()
    {
        ClientboundTrainBaselineBeginPacket begin = null;
        ClientboundTrainBaselineCompletePacket complete = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundTrainBaselineBeginPacket>(packet => begin = packet);
        processor.SubscribeReusable<ClientboundTrainBaselineCompletePacket>(packet => complete = packet);

        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundTrainBaselineBeginPacket
        {
            BaselineId = 91,
            TrainsetCount = 9,
            CarCount = 58,
            Failure = string.Empty
        });
        processor.Write(writer, new ClientboundTrainBaselineCompletePacket
        {
            BaselineId = 91,
            TrainsetCount = 9,
            CarCount = 58
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.Multiple(() =>
        {
            Assert.That(begin.BaselineId, Is.EqualTo(91));
            Assert.That(begin.TrainsetCount, Is.EqualTo(9));
            Assert.That(begin.CarCount, Is.EqualTo(58));
            Assert.That(begin.Failure, Is.Empty);
            Assert.That(complete.BaselineId, Is.EqualTo(begin.BaselineId));
            Assert.That(complete.TrainsetCount, Is.EqualTo(begin.TrainsetCount));
            Assert.That(complete.CarCount, Is.EqualTo(begin.CarCount));
        });
    }

    private static TrainsetSpawnPart Part(ushort netId, string carId) => new(
        netId,
        netId == 27 ? "LocoDE2" : "Flatbed",
        carId,
        "841cebf8-f60a-45ac-b1bf-f374f6d85b71",
        false,
        default,
        false,
        RestorationType.None,
        default(LocoRestorationController.RestorationState),
        0,
        null,
        null,
        null,
        new CustomizationHoleData[0],
        default,
        default,
        4.5f,
        new Vector3(10f, 20f, 30f),
        Quaternion.identity,
        default,
        default,
        default);
}
