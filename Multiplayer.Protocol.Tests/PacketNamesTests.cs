using LiteNetLib.Utils;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Protocol.Serialization;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class PacketNamesTests
{
    // The hash a NetPacketProcessor writes ahead of a packet, which PacketNames reads back.
    private sealed class HashingProcessor : NetPacketProcessor
    {
        public ulong HashOf<T>() => GetHash<T>();
    }

    [Test]
    public void Hash_IsTheOneTheProcessorWrites()
    {
        HashingProcessor processor = new();
        Assert.That(PacketNames.Hash(typeof(CommonChatPacket)), Is.EqualTo(processor.HashOf<CommonChatPacket>()));
        Assert.That(PacketNames.Hash(typeof(ClientboundTrainsetPhysicsPacket)), Is.EqualTo(processor.HashOf<ClientboundTrainsetPhysicsPacket>()));
    }

    [Test]
    public void Describe_NamesAPacketFromItsHash()
    {
        Assert.That(PacketNames.Describe(new HashingProcessor().HashOf<ClientboundTrainsetPhysicsPacket>()),
            Is.EqualTo(nameof(ClientboundTrainsetPhysicsPacket)));
        Assert.That(PacketNames.Describe(42UL), Does.StartWith("packet "));
    }
}
