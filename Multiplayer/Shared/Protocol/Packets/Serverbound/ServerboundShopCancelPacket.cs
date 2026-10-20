namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

// A player pressed Cancel: the basket empties and the cash comes out as a banknote.
public class ServerboundShopCancelPacket
{
    public byte ShopIndex { get; set; }
}
