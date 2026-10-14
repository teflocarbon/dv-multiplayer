namespace Multiplayer.Networking.Packets.Serverbound;

public class ServerboundItemRecallPreparedPacket
{
    public uint OperationId { get; set; }
    public ushort ItemNetId { get; set; }
    public bool Succeeded { get; set; }
    public string FailureReason { get; set; } = string.Empty;
}
