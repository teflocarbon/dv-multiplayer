namespace Multiplayer.Networking.Packets.Serverbound;

public class ServerboundContainerMutationPacket
{
    public uint RequestId { get; set; }
    public byte[] OperationId { get; set; }
    public byte Kind { get; set; }
    public ushort ShellNetId { get; set; }
    public uint SourceContainerHandle { get; set; }
    public uint DestinationContainerHandle { get; set; }
    public uint ExpectedSourceRevision { get; set; }
    public uint ExpectedDestinationRevision { get; set; }
    public int SourceSlot { get; set; }
    public int DestinationSlot { get; set; }
    public ushort ItemNetId { get; set; }
}
