namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Train;

public class ServerboundAddCoalPacket
{
    public ushort NetId { get; set; }
    public float CoalMassDelta { get; set; }
}
