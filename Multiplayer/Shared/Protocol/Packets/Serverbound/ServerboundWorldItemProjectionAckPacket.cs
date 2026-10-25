using Multiplayer.Core.Items.Projection;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

public class ServerboundWorldItemProjectionAckPacket
{
    public ushort ItemNetId { get; set; }
    public uint AuthorityRevision { get; set; }
    public ProjectionAck Kind { get; set; }
}
