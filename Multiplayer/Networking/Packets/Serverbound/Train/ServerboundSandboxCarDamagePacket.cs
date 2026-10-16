namespace Multiplayer.Networking.Packets.Serverbound.Train;

// A client used the comms radio's damage mode on a car: damage it by DamageLevel tenths of its
// health (1-10), or derail it. The host does it, so every player sees it.
public class ServerboundSandboxCarDamagePacket
{
    public ushort CarNetId { get; set; }
    public byte DamageLevel { get; set; }
    public bool Derail { get; set; }
}
