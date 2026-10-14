namespace Multiplayer.Networking.Packets.Clientbound.World;

// Hazmat ground tiles from the host: int count, then per tile int grid coordinates, bool present,
// and (if present) the tile's data in vanilla's own tile format. A tile that's not present has
// been emptied or removed on the host.
public class ClientboundHazmatTilesPacket
{
    public byte[] Data { get; set; }
}
