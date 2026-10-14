namespace Multiplayer.Networking.Packets.Serverbound;

public class ServerboundItemRecallPacket
{
    public ushort ItemNetId { get; set; }
    public uint ExpectedRevision { get; set; }
    public int RequestedSlot { get; set; }
}
