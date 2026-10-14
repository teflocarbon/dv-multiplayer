namespace Multiplayer.Networking.Packets.Serverbound.Train;

public sealed class ServerboundTrainsetRelocationAckPacket
{
    public string OperationId { get; set; }
    public uint Revision { get; set; }
    public ushort RootNetId { get; set; }
    public byte Status { get; set; }
    public string ReasonCode { get; set; }
    public uint AppliedHash { get; set; }
}
