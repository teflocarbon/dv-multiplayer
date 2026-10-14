namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundContainerMutationResultPacket
{
    public uint RequestId { get; set; }
    public byte[] OperationId { get; set; }
    public byte Kind { get; set; }
    public bool Accepted { get; set; }
    public byte Status { get; set; }
    public string RejectionReason { get; set; }
    public uint SourceRevision { get; set; }
    public uint DestinationRevision { get; set; }
    public ushort MaterializedItemNetId { get; set; }
}
