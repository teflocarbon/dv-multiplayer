namespace Multiplayer.Shared.Protocol.Packets.Common;

public class CommonRotateTurntablePacket
{
    public byte NetId { get; set; }
    public float rotation { get; set; }
}
