using LiteNetLib.Utils;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Serverbound;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class WorldItemAuthorityPacketTests
{
    [Test]
    public void CatalogueNegotiationRoundTripsFixedDigestWithoutItemRecords()
    {
        ServerboundWorldItemCataloguePacket received = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ServerboundWorldItemCataloguePacket>(packet => received = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundWorldItemCataloguePacket
        {
            ItemCount = 1432,
            CollisionCount = 0,
            Digest = "0123456789abcdef0123456789abcdef"
        });

        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.Multiple(() =>
        {
            Assert.That(received.ItemCount, Is.EqualTo(1432));
            Assert.That(received.CollisionCount, Is.Zero);
            Assert.That(received.Digest, Is.EqualTo("0123456789abcdef0123456789abcdef"));
        });
    }

    [Test]
    public void ProjectionAcknowledgementUsesSessionIdentityOnly()
    {
        ServerboundWorldItemProjectionAckPacket received = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ServerboundWorldItemProjectionAckPacket>(packet => received = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundWorldItemProjectionAckPacket
        {
            ItemNetId = 735,
            AuthorityRevision = 12,
            Projected = true
        });

        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.Multiple(() =>
        {
            Assert.That(received.ItemNetId, Is.EqualTo(735));
            Assert.That(received.AuthorityRevision, Is.EqualTo(12));
            Assert.That(received.Projected, Is.True);
        });
    }

    [Test]
    public void CatalogueRejectionCarriesHostFingerprintAndReason()
    {
        ClientboundWorldItemCataloguePacket received = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundWorldItemCataloguePacket>(packet => received = packet);
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundWorldItemCataloguePacket
        {
            Accepted = false,
            HostItemCount = 1432,
            HostCollisionCount = 1,
            HostDigest = "fedcba9876543210fedcba9876543210",
            Reason = "catalogue-mismatch"
        });

        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.Multiple(() =>
        {
            Assert.That(received.Accepted, Is.False);
            Assert.That(received.HostItemCount, Is.EqualTo(1432));
            Assert.That(received.HostCollisionCount, Is.EqualTo(1));
            Assert.That(received.Reason, Is.EqualTo("catalogue-mismatch"));
        });
    }
}
