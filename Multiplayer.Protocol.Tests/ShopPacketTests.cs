using LiteNetLib.Utils;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class ShopPacketTests
{
    [Test]
    public void LoadStateUpdate_RoundTripsTheShopCatalogue()
    {
        ServerboundLoadStateUpdatePacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ServerboundLoadStateUpdatePacket>(packet => result = new ServerboundLoadStateUpdatePacket
        {
            LoadState = packet.LoadState,
            TrackGraphHash = packet.TrackGraphHash,
            ShopCatalogueHash = packet.ShopCatalogueHash,
            ShopCount = packet.ShopCount,
            ShopItemCount = packet.ShopItemCount
        });
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundLoadStateUpdatePacket
        {
            LoadState = PlayerLoadingState.ReadyForWorldState,
            TrackGraphHash = 0xF00DCAFE12345678UL,
            ShopCatalogueHash = 0xABCDEF0123456789UL,
            ShopCount = 9,
            ShopItemCount = 64
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result, Is.Not.Null);
        Assert.That(result.TrackGraphHash, Is.EqualTo(0xF00DCAFE12345678UL));
        Assert.That(result.ShopCatalogueHash, Is.EqualTo(0xABCDEF0123456789UL));
        Assert.That((result.ShopCount, result.ShopItemCount), Is.EqualTo(((ushort)9, (ushort)64)));
    }

    [Test]
    public void ShopLayout_RoundTripsANegativeSeed()
    {
        ClientboundShopLayoutPacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundShopLayoutPacket>(packet => result = new ClientboundShopLayoutPacket { Seed = packet.Seed });
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundShopLayoutPacket { Seed = -123456789 });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result?.Seed, Is.EqualTo(-123456789));
    }
}
