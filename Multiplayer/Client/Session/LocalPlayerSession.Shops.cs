using Multiplayer.Client.Shops;
using Multiplayer.Core.Shops;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Shops;

namespace Multiplayer.Client.Session;

// The host's player's shop requests: the same packets a client sends, handed to the server in-process
// (NetworkServer.RegisterLocalShops). The registers' state comes back like anyone's: the host's game
// shows the register from it, as a client's does (docs/design/shops.md).
public partial class LocalPlayerSession : IShopsClient
{
    public void SendShopScan(uint requestId, byte shopIndex, byte moduleIndex) =>
        Submit(new ServerboundShopScanPacket { RequestId = requestId, ShopIndex = shopIndex, ModuleIndex = moduleIndex });

    public void SendShopDeposit(uint requestId, byte shopIndex, ushort moneyNetId) =>
        Submit(new ServerboundShopDepositPacket { RequestId = requestId, ShopIndex = shopIndex, MoneyNetId = moneyNetId });

    public void SendShopBuy(uint requestId, byte shopIndex, uint revision) =>
        Submit(new ServerboundShopBuyPacket { RequestId = requestId, ShopIndex = shopIndex, Revision = revision });

    public void SendShopCancel(uint requestId, byte shopIndex) =>
        Submit(new ServerboundShopCancelPacket { RequestId = requestId, ShopIndex = shopIndex });

    partial void RegisterShops()
    {
        Handle<ClientboundShopRegisterStatePacket>(packet => ShopRegisterView.Apply(packet, PlayerId));
        Handle<ClientboundShopRequestResultPacket>(packet =>
            ShopRegisterView.Refused(packet.ShopIndex, packet.RequestId, (ShopRequestKind)packet.Request, (ShopRefusal)packet.Refusal));
    }
}
