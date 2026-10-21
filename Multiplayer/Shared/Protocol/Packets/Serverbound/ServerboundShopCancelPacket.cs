namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

// A player pressed Cancel: the basket empties and the cash comes out as a banknote.
public class ServerboundShopCancelPacket
{
    // This player's request counter: the answer names it, and its prediction leaves when it comes.
    public uint RequestId { get; set; }
    public byte ShopIndex { get; set; }
}
