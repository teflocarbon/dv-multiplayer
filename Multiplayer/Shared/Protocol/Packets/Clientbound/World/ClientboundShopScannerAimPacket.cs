namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

// Another player's scanner now points at this shelf item (ModuleIndex 255: none), relayed by the
// server to the players near the shop.
public class ClientboundShopScannerAimPacket
{
    public ushort ScannerNetId { get; set; }
    public byte ShopIndex { get; set; }
    public byte ModuleIndex { get; set; }
}
