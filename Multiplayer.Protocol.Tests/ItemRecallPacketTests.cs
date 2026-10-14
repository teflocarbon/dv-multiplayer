using LiteNetLib.Utils;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Serverbound;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class ItemRecallPacketTests
{
    [Test]
    public void Prepare_RoundTripsTransactionIdentityAndCandidateRevision()
    {
        ClientboundItemRecallPreparePacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundItemRecallPreparePacket>(packet => result =
            new ClientboundItemRecallPreparePacket
            {
                OperationId = packet.OperationId,
                ItemNetId = packet.ItemNetId,
                BaseRevision = packet.BaseRevision,
                PreparedRevision = packet.PreparedRevision,
                RequestedSlot = packet.RequestedSlot
            });
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundItemRecallPreparePacket
        {
            OperationId = 91,
            ItemNetId = 766,
            BaseRevision = 42,
            PreparedRevision = 43,
            RequestedSlot = 4
        });

        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result, Is.Not.Null);
        Assert.That(result.OperationId, Is.EqualTo(91));
        Assert.That(result.ItemNetId, Is.EqualTo(766));
        Assert.That(result.BaseRevision, Is.EqualTo(42));
        Assert.That(result.PreparedRevision, Is.EqualTo(43));
        Assert.That(result.RequestedSlot, Is.EqualTo(4));
    }

    [Test]
    public void PreparedResult_RoundTripsFailureWithoutLosingOperationIdentity()
    {
        ServerboundItemRecallPreparedPacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ServerboundItemRecallPreparedPacket>(packet => result =
            new ServerboundItemRecallPreparedPacket
            {
                OperationId = packet.OperationId,
                ItemNetId = packet.ItemNetId,
                Succeeded = packet.Succeeded,
                FailureReason = packet.FailureReason
            });
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundItemRecallPreparedPacket
        {
            OperationId = 92,
            ItemNetId = 737,
            Succeeded = false,
            FailureReason = "recall-claim-restore-failed"
        });

        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result, Is.Not.Null);
        Assert.That(result.OperationId, Is.EqualTo(92));
        Assert.That(result.ItemNetId, Is.EqualTo(737));
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureReason, Is.EqualTo("recall-claim-restore-failed"));
    }

    [Test]
    public void TerminalResult_RoundTripsOperationIdentityAndCommittedRevision()
    {
        ClientboundItemRecallResultPacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundItemRecallResultPacket>(packet => result =
            new ClientboundItemRecallResultPacket
            {
                OperationId = packet.OperationId,
                ItemNetId = packet.ItemNetId,
                Accepted = packet.Accepted,
                AuthorityRevision = packet.AuthorityRevision,
                RejectionReason = packet.RejectionReason
            });
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundItemRecallResultPacket
        {
            OperationId = 93,
            ItemNetId = 738,
            Accepted = true,
            AuthorityRevision = 18,
            RejectionReason = string.Empty
        });

        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result, Is.Not.Null);
        Assert.That(result.OperationId, Is.EqualTo(93));
        Assert.That(result.ItemNetId, Is.EqualTo(738));
        Assert.That(result.Accepted, Is.True);
        Assert.That(result.AuthorityRevision, Is.EqualTo(18));
    }
}
