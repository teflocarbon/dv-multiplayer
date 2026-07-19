using LiteNetLib.Utils;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Serverbound;
using Multiplayer.Networking.Serialization;
using NUnit.Framework;
using UnityEngine;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class ItemSpatialPacketTests
{
    [TestCase(ItemWorldParentKind.World)]
    [TestCase(ItemWorldParentKind.TrainInterior)]
    [TestCase(ItemWorldParentKind.StaticParent)]
    public void MotionSampleRoundTripsEveryParentEncoding(ItemWorldParentKind parentKind)
    {
        ServerboundItemSpatialSamplePacket received = null;
        NetPacketProcessor processor = Processor();
        processor.SubscribeReusable<ServerboundItemSpatialSamplePacket>(packet => received = packet);
        ItemSpatialStateData source = State(parentKind);

        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundItemSpatialSamplePacket { State = source });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        AssertState(received.State, source);
    }

    [Test]
    public void ReliableSettlementPreservesFinalSequenceAndSleepingState()
    {
        ServerboundItemSpatialSettlementPacket received = null;
        NetPacketProcessor processor = Processor();
        processor.SubscribeReusable<ServerboundItemSpatialSettlementPacket>(packet => received = packet);
        ItemSpatialStateData source = State(ItemWorldParentKind.TrainInterior);
        source.Phase = ItemSpatialPhase.Settled;
        source.SampleSequence = 91;
        source.Sleeping = true;

        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundItemSpatialSettlementPacket { State = source });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        AssertState(received.State, source);
        Assert.That(received.State.Sleeping, Is.True);
    }

    [Test]
    public void LeaseGrantAndRevocationCarryTheEpochBaseline()
    {
        ClientboundItemSpatialLeasePacket received = null;
        NetPacketProcessor processor = Processor();
        processor.SubscribeReusable<ClientboundItemSpatialLeasePacket>(packet => received = packet);
        ItemSpatialStateData source = State(ItemWorldParentKind.World);

        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundItemSpatialLeasePacket
        {
            Active = true,
            Reason = "world-release",
            State = source
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(received.Active, Is.True);
        Assert.That(received.Reason, Is.EqualTo("world-release"));
        AssertState(received.State, source);
    }

    [Test]
    public void RelayedSamplePreservesHostAcceptedSpatialIdentity()
    {
        ClientboundItemSpatialSamplePacket received = null;
        NetPacketProcessor processor = Processor();
        processor.SubscribeReusable<ClientboundItemSpatialSamplePacket>(packet => received = packet);
        ItemSpatialStateData source = State(ItemWorldParentKind.StaticParent);

        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundItemSpatialSamplePacket { State = source });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        AssertState(received.State, source);
    }

    [Test]
    public void SpatialCommitCarriesIncrementedAuthorityRevisionAndSettledPose()
    {
        ClientboundItemSpatialCommitPacket received = null;
        NetPacketProcessor processor = Processor();
        processor.SubscribeReusable<ClientboundItemSpatialCommitPacket>(packet => received = packet);
        ItemSpatialStateData source = State(ItemWorldParentKind.World);
        source.AuthorityRevision = 42;
        source.Phase = ItemSpatialPhase.Settled;
        source.Sleeping = true;

        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundItemSpatialCommitPacket { State = source });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        AssertState(received.State, source);
        Assert.That(received.State.AuthorityRevision, Is.EqualTo(42));
    }

    [Test]
    public void TrainWakeWitnessPreservesEpochAndPhysicalEvidence()
    {
        ServerboundItemTrainWakeWitnessPacket received = null;
        NetPacketProcessor processor = Processor();
        processor.SubscribeReusable<ServerboundItemTrainWakeWitnessPacket>(packet => received = packet);
        ServerboundItemTrainWakeWitnessPacket source = new()
        {
            WitnessId = 77,
            SourceItemNetId = 701,
            SourceAuthorityRevision = 8,
            SourceSimulationEpoch = 3,
            TargetItemNetId = 702,
            TargetAuthorityRevision = 11,
            TrainCarNetId = 44,
            SourceTick = 912,
            RelativeVelocity = new Vector3(3f, -0.5f, 1f),
            Impulse = new Vector3(1.5f, 0.25f, 0.5f),
            AbsoluteContactPoint = new Vector3(100f, 4f, 200f)
        };

        NetDataWriter writer = new();
        processor.Write(writer, source);
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.Multiple(() =>
        {
            Assert.That(received.WitnessId, Is.EqualTo(source.WitnessId));
            Assert.That(received.SourceItemNetId, Is.EqualTo(source.SourceItemNetId));
            Assert.That(received.SourceAuthorityRevision, Is.EqualTo(source.SourceAuthorityRevision));
            Assert.That(received.SourceSimulationEpoch, Is.EqualTo(source.SourceSimulationEpoch));
            Assert.That(received.TargetItemNetId, Is.EqualTo(source.TargetItemNetId));
            Assert.That(received.TargetAuthorityRevision, Is.EqualTo(source.TargetAuthorityRevision));
            Assert.That(received.TrainCarNetId, Is.EqualTo(source.TrainCarNetId));
            Assert.That(received.SourceTick, Is.EqualTo(source.SourceTick));
            Assert.That(received.RelativeVelocity, Is.EqualTo(source.RelativeVelocity));
            Assert.That(received.Impulse, Is.EqualTo(source.Impulse));
            Assert.That(received.AbsoluteContactPoint, Is.EqualTo(source.AbsoluteContactPoint));
        });
    }

    private static NetPacketProcessor Processor()
    {
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        return processor;
    }

    private static ItemSpatialStateData State(ItemWorldParentKind parentKind) => new()
    {
        ItemNetId = 735,
        AuthorityRevision = 17,
        SimulationEpoch = 4,
        SampleSequence = 23,
        SourceTick = 991,
        SimulatorPlayerId = 2,
        Phase = ItemSpatialPhase.InFlight,
        AbsolutePosition = new Vector3(9475.25f, 119.5f, 13616.75f),
        Rotation = new Quaternion(0.11f, 0.27f, 0.03f, 0.95f),
        LinearVelocity = new Vector3(2.5f, -1.25f, 6.75f),
        AngularVelocity = new Vector3(0.2f, 0.4f, -0.1f),
        WorldParentKind = parentKind,
        WorldParentNetId = parentKind == ItemWorldParentKind.TrainInterior ? (ushort)88 : (ushort)0,
        WorldParentKey = parentKind == ItemWorldParentKind.StaticParent ? "offices/steel-mill/table" : string.Empty,
        ParentLocalPosition = new Vector3(0.3f, 1.1f, -0.6f),
        ParentLocalRotation = new Quaternion(0f, 0.7071068f, 0f, 0.7071068f),
        Sleeping = false
    };

    private static void AssertState(ItemSpatialStateData actual, ItemSpatialStateData expected)
    {
        Assert.That(actual, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(actual.ItemNetId, Is.EqualTo(expected.ItemNetId));
            Assert.That(actual.AuthorityRevision, Is.EqualTo(expected.AuthorityRevision));
            Assert.That(actual.SimulationEpoch, Is.EqualTo(expected.SimulationEpoch));
            Assert.That(actual.SampleSequence, Is.EqualTo(expected.SampleSequence));
            Assert.That(actual.SourceTick, Is.EqualTo(expected.SourceTick));
            Assert.That(actual.SimulatorPlayerId, Is.EqualTo(expected.SimulatorPlayerId));
            Assert.That(actual.Phase, Is.EqualTo(expected.Phase));
            Assert.That(actual.AbsolutePosition, Is.EqualTo(expected.AbsolutePosition));
            Assert.That(actual.Rotation, Is.EqualTo(expected.Rotation));
            Assert.That(actual.LinearVelocity, Is.EqualTo(expected.LinearVelocity));
            Assert.That(actual.AngularVelocity, Is.EqualTo(expected.AngularVelocity));
            Assert.That(actual.WorldParentKind, Is.EqualTo(expected.WorldParentKind));
            Assert.That(actual.WorldParentNetId, Is.EqualTo(expected.WorldParentNetId));
            Assert.That(actual.WorldParentKey ?? string.Empty, Is.EqualTo(expected.WorldParentKey ?? string.Empty));
            Assert.That(actual.ParentLocalPosition, Is.EqualTo(expected.WorldParentKind == ItemWorldParentKind.World
                ? Vector3.zero : expected.ParentLocalPosition));
            Assert.That(actual.ParentLocalRotation, Is.EqualTo(expected.WorldParentKind == ItemWorldParentKind.World
                ? default(Quaternion) : expected.ParentLocalRotation));
            Assert.That(actual.Sleeping, Is.EqualTo(expected.Sleeping));
        });
    }
}
