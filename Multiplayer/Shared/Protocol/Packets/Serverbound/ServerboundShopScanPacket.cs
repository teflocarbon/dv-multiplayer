namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

// A player scanned a shelf item: one more unit of it in the shop register's basket, owned by them.
public class ServerboundShopScanPacket
{
    // This player's request counter: the answer names it, and its prediction leaves when it comes.
    public uint RequestId { get; set; }
    public byte ShopIndex { get; set; }
    public byte ModuleIndex { get; set; }
}
