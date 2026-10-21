using LiteNetLib.Utils;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Packets.Clientbound.SaveGame;
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
    public void RegisterState_RoundTripsTheBasket()
    {
        ClientboundShopRegisterStatePacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundShopRegisterStatePacket>(packet => result = new ClientboundShopRegisterStatePacket
        {
            ShopIndex = packet.ShopIndex,
            Revision = packet.Revision,
            LineModules = packet.LineModules,
            LineOwners = packet.LineOwners,
            LineUnits = packet.LineUnits,
            Deposited = packet.Deposited,
            Delivering = packet.Delivering,
            Event = packet.Event,
            EventPlayerId = packet.EventPlayerId,
            EventModule = packet.EventModule
        });
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundShopRegisterStatePacket
        {
            ShopIndex = 3,
            Revision = 4_000_000_001,
            LineModules = new byte[] { 0, 7 },
            LineOwners = new byte[] { 2, 1 },
            LineUnits = new byte[] { 1, 3 },
            Deposited = 1234.56,
            Delivering = true,
            Event = 4,
            EventPlayerId = 2,
            EventModule = 7
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result, Is.Not.Null);
        Assert.That((result.ShopIndex, result.Revision), Is.EqualTo(((byte)3, 4_000_000_001u)));
        Assert.That(result.LineModules, Is.EqualTo(new byte[] { 0, 7 }));
        Assert.That(result.LineOwners, Is.EqualTo(new byte[] { 2, 1 }));
        Assert.That(result.LineUnits, Is.EqualTo(new byte[] { 1, 3 }));
        Assert.That(result.Deposited, Is.EqualTo(1234.56));
        Assert.That(result.Delivering, Is.True);
        Assert.That((result.Event, result.EventPlayerId, result.EventModule), Is.EqualTo(((byte)4, (byte)2, (byte)7)));
    }

    [Test]
    public void RegisterState_RoundTripsAnEmptyBasket()
    {
        ClientboundShopRegisterStatePacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundShopRegisterStatePacket>(packet => result = new ClientboundShopRegisterStatePacket
        {
            LineModules = packet.LineModules,
            LineUnits = packet.LineUnits
        });
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundShopRegisterStatePacket
        {
            LineModules = new byte[0],
            LineOwners = new byte[0],
            LineUnits = new byte[0]
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result.LineModules, Is.Empty);
        Assert.That(result.LineUnits, Is.Empty);
    }

    [Test]
    public void ScannerAim_RoundTripsBothWays()
    {
        ServerboundShopScannerAimPacket request = null;
        ClientboundShopScannerAimPacket relay = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ServerboundShopScannerAimPacket>(p => request = new ServerboundShopScannerAimPacket
            { ScannerNetId = p.ScannerNetId, ShopIndex = p.ShopIndex, ModuleIndex = p.ModuleIndex });
        processor.SubscribeReusable<ClientboundShopScannerAimPacket>(p => relay = new ClientboundShopScannerAimPacket
            { ScannerNetId = p.ScannerNetId, ShopIndex = p.ShopIndex, ModuleIndex = p.ModuleIndex });
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundShopScannerAimPacket { ScannerNetId = 186, ShopIndex = 2, ModuleIndex = 17 });
        processor.Write(writer, new ClientboundShopScannerAimPacket { ScannerNetId = 186, ShopIndex = 2, ModuleIndex = 255 });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That((request.ScannerNetId, request.ShopIndex, request.ModuleIndex), Is.EqualTo(((ushort)186, (byte)2, (byte)17)));
        Assert.That((relay.ScannerNetId, relay.ShopIndex, relay.ModuleIndex), Is.EqualTo(((ushort)186, (byte)2, (byte)255)));
    }

    [Test]
    public void Money_KeepsTheCentsOfALargeBalance()
    {
        ClientboundMoneyPacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundMoneyPacket>(packet =>
            result = new() { Amount = packet.Amount, Revision = packet.Revision });
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundMoneyPacket { Amount = 12_345_678.91, Revision = 42 });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result.Amount, Is.EqualTo(12_345_678.91));
        Assert.That(result.Revision, Is.EqualTo(42u));
    }

    [Test]
    public void ShopStock_RoundTripsTheAllowedAmounts()
    {
        ClientboundShopStockPacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundShopStockPacket>(packet => result = new ClientboundShopStockPacket
        {
            ItemPrefabNames = packet.ItemPrefabNames, PurchasedItems = packet.PurchasedItems, AllowedToHaveAmount = packet.AllowedToHaveAmount
        });
        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundShopStockPacket
        {
            ItemPrefabNames = new[] { "shovel", "WirelessMUController" }, PurchasedItems = new[] { 1, 0 }, AllowedToHaveAmount = new[] { 2, 0 }
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result.ItemPrefabNames, Is.EqualTo(new[] { "shovel", "WirelessMUController" }));
        Assert.That(result.PurchasedItems, Is.EqualTo(new[] { 1, 0 }));
        Assert.That(result.AllowedToHaveAmount, Is.EqualTo(new[] { 2, 0 }));
    }

    [Test]
    public void ShopRequests_RoundTripTheirRequestIdsAndTheAnswersNameThem()
    {
        ServerboundShopScanPacket scan = null;
        ClientboundShopRequestResultPacket refusal = null;
        ClientboundShopRegisterStatePacket state = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ServerboundShopScanPacket>(p => scan = new ServerboundShopScanPacket
            { RequestId = p.RequestId, ShopIndex = p.ShopIndex, ModuleIndex = p.ModuleIndex });
        processor.SubscribeReusable<ClientboundShopRequestResultPacket>(p => refusal = new ClientboundShopRequestResultPacket
            { RequestId = p.RequestId, ShopIndex = p.ShopIndex, Request = p.Request, Refusal = p.Refusal });
        processor.SubscribeReusable<ClientboundShopRegisterStatePacket>(p => state = new ClientboundShopRegisterStatePacket
            { AckPlayerId = p.AckPlayerId, AckRequestId = p.AckRequestId });
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundShopScanPacket { RequestId = 3_000_000_001, ShopIndex = 1, ModuleIndex = 9 });
        processor.Write(writer, new ClientboundShopRequestResultPacket { RequestId = 77, ShopIndex = 1, Request = 0, Refusal = 3 });
        processor.Write(writer, new ClientboundShopRegisterStatePacket
        {
            LineModules = new byte[0], LineOwners = new byte[0], LineUnits = new byte[0], AckPlayerId = 2, AckRequestId = 78
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That((scan.RequestId, scan.ShopIndex, scan.ModuleIndex), Is.EqualTo((3_000_000_001u, (byte)1, (byte)9)));
        Assert.That((refusal.RequestId, refusal.Refusal), Is.EqualTo((77u, (byte)3)));
        Assert.That((state.AckPlayerId, state.AckRequestId), Is.EqualTo(((byte)2, 78u)));
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
