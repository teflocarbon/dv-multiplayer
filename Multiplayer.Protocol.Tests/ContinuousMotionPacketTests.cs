using LiteNetLib.Utils;
using Multiplayer.Networking.Data.Player;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Networking.Serialization;
using NUnit.Framework;
using UnityEngine;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class ContinuousMotionPacketTests
{
    [Test]
    public void TrainPhysicsRoundTripPreservesEveryMovementPartAndMetresPerSecondSpeed()
    {
        ClientboundTrainsetPhysicsPacket received = null;
        NetPacketProcessor processor = Processor();
        processor.SubscribeReusable<ClientboundTrainsetPhysicsPacket>(packet => received = packet);

        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundTrainsetPhysicsPacket
        {
            FirstNetId = 41,
            LastNetId = 50,
            Tick = 8192,
            TrainsetParts = new[]
            {
                new TrainsetMovementPart(41, 19.444445f, 0.15f, default, default,
                    new Vector3(100f, 5f, 200f), Quaternion.identity),
                new TrainsetMovementPart(50, -3.5f, 0.2f, default, default)
            }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(received.TrainsetParts, Has.Length.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(received.Tick, Is.EqualTo(8192));
            Assert.That(received.TrainsetParts[0], Is.Not.Null);
            Assert.That(received.TrainsetParts[0].NetId, Is.EqualTo(41));
            Assert.That(received.TrainsetParts[0].Speed, Is.EqualTo(19.444445f));
            Assert.That(received.TrainsetParts[0].Position, Is.EqualTo(new Vector3(100f, 5f, 200f)));
            Assert.That(received.TrainsetParts[1], Is.Not.Null);
            Assert.That(received.TrainsetParts[1].NetId, Is.EqualTo(50));
            Assert.That(received.TrainsetParts[1].Speed, Is.EqualTo(-3.5f));
        });
    }

    [Test]
    public void HostPlayerTrackingRoundTripPreservesPoseAndVrHands()
    {
        ClientboundPlayerPositionPacket received = null;
        NetPacketProcessor processor = Processor();
        processor.SubscribeReusable<ClientboundPlayerPositionPacket>(packet => received = packet);

        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundPlayerPositionPacket
        {
            PlayerId = 1,
            IsOnCar = true,
            CarID = 41,
            Posture = PlayerPostureFlags.None,
            TrackingData = new PlayerTrackingData
            {
                Position = new Vector3(0.5f, 2f, -1f),
                RotationY = 137f,
                LookPosition = -12f,
                LeftHandPosition = new Vector3(-0.3f, 1.2f, 0.4f),
                RightHandPosition = new Vector3(0.3f, 1.1f, 0.5f)
            }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.Multiple(() =>
        {
            Assert.That(received.PlayerId, Is.EqualTo(1));
            Assert.That(received.IsOnCar, Is.True);
            Assert.That(received.CarID, Is.EqualTo(41));
            Assert.That(received.TrackingData.Position, Is.EqualTo(new Vector3(0.5f, 2f, -1f)));
            Assert.That(received.TrackingData.RotationY, Is.EqualTo(137f));
            Assert.That(received.TrackingData.LeftHandPosition, Is.EqualTo(new Vector3(-0.3f, 1.2f, 0.4f)));
            Assert.That(received.TrackingData.RightHandPosition, Is.EqualTo(new Vector3(0.3f, 1.1f, 0.5f)));
        });
    }

    private static NetPacketProcessor Processor()
    {
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        return processor;
    }
}
