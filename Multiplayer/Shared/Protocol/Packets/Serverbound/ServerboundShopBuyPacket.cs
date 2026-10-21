namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

// A player pressed Buy on the basket they saw (Revision): the server refuses a basket that changed since.
public class ServerboundShopBuyPacket
{
    // This player's request counter: the answer names it, and its prediction leaves when it comes.
    public uint RequestId { get; set; }
    public byte ShopIndex { get; set; }
    public uint Revision { get; set; }
}
