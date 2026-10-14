namespace Multiplayer.Networking.Packets.Common.Train;

public class CommonTrainPortsPacket
{
    public ulong OperationId { get; set; }
    public uint ExpectedRevision { get; set; }
    public ushort NetId { get; set; }
    public uint[] PortIds { get; set; }
    public float[] PortValues { get; set; }
}
