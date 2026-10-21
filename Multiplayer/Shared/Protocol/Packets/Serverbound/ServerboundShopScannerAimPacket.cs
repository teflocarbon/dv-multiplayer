namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

// The shelf item a player's scanner now points at (ModuleIndex 255: none), sent by the holder's game
// when it changes: every game shows the same scanner screen and hears its hover sound.
public class ServerboundShopScannerAimPacket
{
    public ushort ScannerNetId { get; set; }
    public byte ShopIndex { get; set; }
    public byte ModuleIndex { get; set; }
}
