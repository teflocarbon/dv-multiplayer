namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundItemRecallResultPacket
{
    public ushort ItemNetId { get; set; }
    public bool Accepted { get; set; }
    public uint AuthorityRevision { get; set; }
    public string RejectionReason { get; set; }
}
