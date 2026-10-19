using LiteNetLib.Utils;
using Multiplayer.Core.Trains;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Train;
using Multiplayer.Shared.Protocol.Serialization;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class TrainTopologyPacketTests
{
    [Test]
    public void SnapshotRoundTripPreservesPartnersLinesAndRevision()
    {
        ClientboundTrainTopologySnapshotPacket received = null;
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        processor.SubscribeReusable<ClientboundTrainTopologySnapshotPacket>(packet => received = packet);

        TrainEndpointStateData first = Endpoint(11, TrainEnd.Front, 22, TrainEnd.Rear);
        TrainEndpointStateData second = Endpoint(22, TrainEnd.Rear, 11, TrainEnd.Front);
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundTrainTopologySnapshotPacket
        {
            BaselineId = 91,
            TopologyRevision = 417,
            OperationId = 0x1122334455667788UL,
            Result = TrainTopologyResultCode.Accepted,
            Endpoints = new[] { first, second },
            BrakeLines = new[]
            {
                new BrakeLineStateData
                {
                    AnchorCarNetId = 11,
                    AnchorEnd = TrainEnd.Front,
                    MemberDigest = 0xAABBCCDD,
                    PipePressure = 5.35f
                }
            },
            ControlRevisions = new[]
            {
                new TrainControlRevisionData { CarNetId = 11, Revision = 73 }
            },
            ControlStates = new[]
            {
                new TrainControlStateData
                {
                    CarNetId = 11,
                    Revision = 73,
                    PortIds = new uint[] { 8, 9 },
                    PortValues = new[] { 0.25f, 0.75f },
                    FuseIds = new uint[] { 14 },
                    FuseValues = new[] { true },
                    HasHandbrake = true,
                    HandbrakePosition = 0.5f
                }
            }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(received, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(received.BaselineId, Is.EqualTo(91));
            Assert.That(received.TopologyRevision, Is.EqualTo(417));
            Assert.That(received.OperationId, Is.EqualTo(0x1122334455667788UL));
            Assert.That(received.Result, Is.EqualTo(TrainTopologyResultCode.Accepted));
            Assert.That(received.Endpoints, Has.Length.EqualTo(2));
            Assert.That(received.Endpoints[0].MechanicalPartnerCarNetId, Is.EqualTo(22));
            Assert.That(received.Endpoints[0].HosePartnerCarNetId, Is.EqualTo(22));
            Assert.That(received.Endpoints[0].MultipleUnitPartnerCarNetId, Is.EqualTo(22));
            Assert.That(received.Endpoints[1].MechanicalPartnerCarNetId, Is.EqualTo(11));
            Assert.That(received.BrakeLines, Has.Length.EqualTo(1));
            Assert.That(received.BrakeLines[0].MemberDigest, Is.EqualTo(0xAABBCCDD));
            Assert.That(received.BrakeLines[0].PipePressure, Is.EqualTo(5.35f));
            Assert.That(received.ControlRevisions, Has.Length.EqualTo(1));
            Assert.That(received.ControlRevisions[0].Revision, Is.EqualTo(73));
            Assert.That(received.ControlStates, Has.Length.EqualTo(1));
            Assert.That(received.ControlStates[0].PortValues[1], Is.EqualTo(0.75f));
            Assert.That(received.ControlStates[0].FuseValues, Is.EqualTo(new[] { true }));
            Assert.That(received.ControlStates[0].HandbrakePosition, Is.EqualTo(0.5f));
        });
    }

    [Test]
    public void CommandRoundTripPreservesExpectedRevisionAndCascadeSemantics()
    {
        ServerboundTrainTopologyCommandPacket received = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ServerboundTrainTopologyCommandPacket>(packet => received = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundTrainTopologyCommandPacket
        {
            OperationId = 77,
            ExpectedRevision = 19,
            Kind = TrainTopologyCommandKind.Uncouple,
            PrimaryCarNetId = 8,
            PrimaryEnd = TrainEnd.Rear,
            HasSecondary = true,
            SecondaryCarNetId = 9,
            SecondaryEnd = TrainEnd.Front,
            CascadeHose = true,
            CascadeMultipleUnit = true,
            PlayAudio = true
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(received, Is.Not.Null);
        TrainTopologyCommand core = received.ToCore();
        Assert.Multiple(() =>
        {
            Assert.That(core.OperationId, Is.EqualTo(77));
            Assert.That(core.ExpectedRevision, Is.EqualTo(19));
            Assert.That(core.Primary, Is.EqualTo(new TrainEndpointId(8, TrainEnd.Rear)));
            Assert.That(core.Secondary, Is.EqualTo(new TrainEndpointId(9, TrainEnd.Front)));
            Assert.That(core.CascadeHose, Is.True);
            Assert.That(core.CascadeMultipleUnit, Is.True);
            Assert.That(received.PlayAudio, Is.True);
        });
    }

    private static TrainEndpointStateData Endpoint(ushort car, TrainEnd end,
        ushort partnerCar, TrainEnd partnerEnd) => new()
    {
        CarNetId = car,
        End = end,
        Capabilities = TrainEndpointCapabilities.Mechanical | TrainEndpointCapabilities.Hose |
            TrainEndpointCapabilities.Cock | TrainEndpointCapabilities.MultipleUnit,
        HasMechanicalPartner = true,
        MechanicalPartnerCarNetId = partnerCar,
        MechanicalPartnerEnd = partnerEnd,
        HasHosePartner = true,
        HosePartnerCarNetId = partnerCar,
        HosePartnerEnd = partnerEnd,
        HasMultipleUnitPartner = true,
        MultipleUnitPartnerCarNetId = partnerCar,
        MultipleUnitPartnerEnd = partnerEnd,
        ChainState = TrainChainState.AttachedTight,
        CockOpen = true
    };
}
