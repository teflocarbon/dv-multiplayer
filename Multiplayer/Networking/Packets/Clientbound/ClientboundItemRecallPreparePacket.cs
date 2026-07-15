namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundItemRecallPreparePacket
{
    public uint OperationId { get; set; }
    public ushort ItemNetId { get; set; }
    public uint BaseRevision { get; set; }
    public uint PreparedRevision { get; set; }
    public int RequestedSlot { get; set; }
}
