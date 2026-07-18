namespace Multiplayer.Networking.Packets.Serverbound;

public class ServerboundWorldItemProjectionAckPacket
{
    public ushort ItemNetId { get; set; }
    public uint AuthorityRevision { get; set; }
    public bool Projected { get; set; }
}
