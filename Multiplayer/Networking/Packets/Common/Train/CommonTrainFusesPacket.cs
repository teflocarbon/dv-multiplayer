namespace Multiplayer.Networking.Packets.Common.Train;

public class CommonTrainFusesPacket
{
    public ulong OperationId { get; set; }
    public uint ExpectedRevision { get; set; }
    public ushort NetId { get; set; }
    public uint[] FuseIds { get; set; }
    public bool[] FuseValues { get; set; }
}
