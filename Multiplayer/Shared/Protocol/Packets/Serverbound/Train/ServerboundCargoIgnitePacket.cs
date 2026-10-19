namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Train;

// A client's lighter or flare reached a car's cargo: light it (or, for explosive cargo the igniter
// set off, make it explode). The host decides, on its own copy.
public class ServerboundCargoIgnitePacket
{
    public ushort CarNetId { get; set; }
    public byte Index { get; set; }  // which of the car's cargo reactions
    public float Strength { get; set; }
    public bool Explode { get; set; }
}
