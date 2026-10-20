namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

// A player pressed Buy on the basket they saw (Revision): the server refuses a basket that changed since.
public class ServerboundShopBuyPacket
{
    public byte ShopIndex { get; set; }
    public uint Revision { get; set; }
}
