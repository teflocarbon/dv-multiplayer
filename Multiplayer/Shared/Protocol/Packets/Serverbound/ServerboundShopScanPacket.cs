namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

// A player scanned a shelf item: one more unit of it in the shop register's basket, owned by them.
public class ServerboundShopScanPacket
{
    public byte ShopIndex { get; set; }
    public byte ModuleIndex { get; set; }
}
