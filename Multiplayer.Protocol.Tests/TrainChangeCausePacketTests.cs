using System;
using LiteNetLib.Utils;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Networking.Serialization;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class TrainChangeCausePacketTests
{
    [Test]
    public void SnapshotRoundTripPreservesRefusalDetailAndUncoupleCauses()
    {
        ClientboundTrainTopologySnapshotPacket received = RoundTrip(new ClientboundTrainTopologySnapshotPacket
        {
            TopologyRevision = 12,
            OperationId = 0x0500000000000001UL,
            Result = TrainTopologyResultCode.OutOfRange,
            Endpoints = Array.Empty<TrainEndpointStateData>(),
            BrakeLines = Array.Empty<BrakeLineStateData>(),
            ControlRevisions = Array.Empty<TrainControlRevisionData>(),
            ControlStates = Array.Empty<TrainControlStateData>(),
            Detail = "the host has the player 24.1 m from CGS866's centre (limit 15.0 m)",
            Uncouples = new[]
            {
                new TrainUncoupleCauseData
                {
                    CarNetId = 109,
                    End = TrainEnd.Front,
                    Cause = new ChangeCause<UncoupleReason>(UncoupleReason.CarRemoved, 3, "Bentgear29", 4410,
                        "CFF439 PlayerDeleted")
                }
            }
        });

        Assert.That(received.Detail, Does.StartWith("the host has the player 24.1 m"));
        Assert.That(received.Uncouples, Has.Length.EqualTo(1));
        TrainUncoupleCauseData uncouple = received.Uncouples[0];
        Assert.That(uncouple.CarNetId, Is.EqualTo(109));
        Assert.That(uncouple.End, Is.EqualTo(TrainEnd.Front));
        Assert.That(uncouple.Cause.Reason, Is.EqualTo(UncoupleReason.CarRemoved));
        Assert.That(uncouple.Cause.PlayerId, Is.EqualTo(3));
        Assert.That(uncouple.Cause.PlayerName, Is.EqualTo("Bentgear29"));
        Assert.That(uncouple.Cause.Tick, Is.EqualTo(4410));
        Assert.That(uncouple.Cause.Detail, Is.EqualTo("CFF439 PlayerDeleted"));
    }

    [Test]
    public void DestroyRoundTripPreservesRemovalCause()
    {
        ClientboundDestroyTrainCarPacket received = RoundTrip(new ClientboundDestroyTrainCarPacket
        {
            NetId = 112,
            Cause = new ChangeCause<CarRemovalReason>(CarRemovalReason.PlayerDeleted, 3, "Bentgear29", 9)
        });

        Assert.That(received.NetId, Is.EqualTo(112));
        Assert.That(received.Cause.ToString(), Is.EqualTo("PlayerDeleted by Bentgear29"));
    }

    [Test]
    public void DerailCauseRoundTripsWithoutAPlayer()
    {
        ClientboundTrainDerailCausePacket received = RoundTrip(new ClientboundTrainDerailCausePacket
        {
            NetId = 65,
            Cause = new ChangeCause<DerailReason>(DerailReason.Stress, detail: "Due to stress build up: 0.61 at speed: 54.9")
        });

        Assert.That(received.NetId, Is.EqualTo(65));
        Assert.That(received.Cause.Reason, Is.EqualTo(DerailReason.Stress));
        Assert.That(received.Cause.HasPlayer, Is.False);
        Assert.That(received.Cause.Detail, Does.Contain("54.9"));
    }

    private static T RoundTrip<T>(T packet) where T : class, new()
    {
        T received = null;
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        processor.SubscribeReusable<T>(value => received = value);
        NetDataWriter writer = new();
        processor.Write(writer, packet);
        processor.ReadAllPackets(new NetDataReader(writer));
        Assert.That(received, Is.Not.Null);
        return received;
    }
}
