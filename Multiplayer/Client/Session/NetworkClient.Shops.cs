using LiteNetLib;
using Multiplayer.Client.Shops;
using Multiplayer.Core.Shops;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Shops;

namespace Multiplayer.Client.Session;

// NetworkClient's shop requests and the registers' state (see docs/design/shops.md).
public partial class NetworkClient : IShopsClient
{
    public void SendShopScan(byte shopIndex, byte moduleIndex) =>
        SendPacketToServer(new ServerboundShopScanPacket { ShopIndex = shopIndex, ModuleIndex = moduleIndex }, DeliveryMethod.ReliableOrdered);

    public void SendShopDeposit(byte shopIndex, ushort moneyNetId) =>
        SendPacketToServer(new ServerboundShopDepositPacket { ShopIndex = shopIndex, MoneyNetId = moneyNetId }, DeliveryMethod.ReliableOrdered);

    public void SendShopBuy(byte shopIndex, uint revision) =>
        SendPacketToServer(new ServerboundShopBuyPacket { ShopIndex = shopIndex, Revision = revision }, DeliveryMethod.ReliableOrdered);

    public void SendShopCancel(byte shopIndex) =>
        SendPacketToServer(new ServerboundShopCancelPacket { ShopIndex = shopIndex }, DeliveryMethod.ReliableOrdered);

    private void OnClientboundShopRegisterStatePacket(ClientboundShopRegisterStatePacket packet) =>
        ShopRegisterView.Apply(packet, PlayerId);

    private void OnClientboundShopRequestResultPacket(ClientboundShopRequestResultPacket packet)
    {
        LogDebug(() => $"Shop {packet.ShopIndex}: {(ShopRequestKind)packet.Request} refused: {(ShopRefusal)packet.Refusal}");
        ShopRegisterView.ShowRefusal((ShopRefusal)packet.Refusal);
    }
}
