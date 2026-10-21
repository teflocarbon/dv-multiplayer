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
    public void SendShopScan(uint requestId, byte shopIndex, byte moduleIndex) =>
        SendPacketToServer(new ServerboundShopScanPacket { RequestId = requestId, ShopIndex = shopIndex, ModuleIndex = moduleIndex }, DeliveryMethod.ReliableOrdered);

    public void SendShopDeposit(uint requestId, byte shopIndex, ushort moneyNetId) =>
        SendPacketToServer(new ServerboundShopDepositPacket { RequestId = requestId, ShopIndex = shopIndex, MoneyNetId = moneyNetId }, DeliveryMethod.ReliableOrdered);

    public void SendShopBuy(uint requestId, byte shopIndex, uint revision) =>
        SendPacketToServer(new ServerboundShopBuyPacket { RequestId = requestId, ShopIndex = shopIndex, Revision = revision }, DeliveryMethod.ReliableOrdered);

    public void SendShopCancel(uint requestId, byte shopIndex) =>
        SendPacketToServer(new ServerboundShopCancelPacket { RequestId = requestId, ShopIndex = shopIndex }, DeliveryMethod.ReliableOrdered);

    private void OnClientboundShopRegisterStatePacket(ClientboundShopRegisterStatePacket packet) =>
        ShopRegisterView.Apply(packet, PlayerId);

    private void OnClientboundShopRequestResultPacket(ClientboundShopRequestResultPacket packet)
    {
        LogDebug(() => $"Shop {packet.ShopIndex}: {(ShopRequestKind)packet.Request} refused: {(ShopRefusal)packet.Refusal}");
        ShopRegisterView.Refused(packet.ShopIndex, packet.RequestId, (ShopRequestKind)packet.Request, (ShopRefusal)packet.Refusal);
    }
}
