namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

// Cash a client paid into a shop's register (positive) or got back from it (negative: change, or
// a cancelled basket). Shop purchases run on the client, but the wallet is shared and host-owned,
// so the host moves the money.
public class ServerboundShopPaymentPacket
{
    public double Amount { get; set; }
}
