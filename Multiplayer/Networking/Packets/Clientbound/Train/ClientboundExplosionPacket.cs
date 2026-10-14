namespace Multiplayer.Networking.Packets.Clientbound.Train;

// A car exploded on the host. Clients play the same explosion (effects, sound, burnt-out model,
// the push on their own player) but leave the trains alone: the host's physics does that, and the
// result reaches them through the normal train sync.
public class ClientboundExplosionPacket
{
    public ushort CarNetId { get; set; }
    public byte Kind { get; set; }   // ExplosionSync.Kind
    public byte Index { get; set; }  // which of the car's components of that kind
}
