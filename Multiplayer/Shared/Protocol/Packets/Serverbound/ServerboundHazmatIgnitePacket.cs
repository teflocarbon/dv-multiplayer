namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

// A client tried to set a hazmat ground tile alight (a lighter, flare or burning item over a
// spill). The ground is the host's, so the host does the igniting.
public class ServerboundHazmatIgnitePacket
{
    public int GridCoords { get; set; }
    public float Strength { get; set; }
}
