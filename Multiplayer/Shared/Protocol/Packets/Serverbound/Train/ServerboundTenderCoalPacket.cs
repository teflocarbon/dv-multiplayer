namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Train;

public class ServerboundTenderCoalPacket
{
    public ushort NetId { get; set; }
    public float CoalMassDelta { get; set; }
}
