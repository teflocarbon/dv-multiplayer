namespace Multiplayer.Shared.Protocol.Packets.Common.Train;

public class CommonBrakeCylinderReleasePacket
{
    public ulong OperationId { get; set; }
    public uint ExpectedRevision { get; set; }
    public ushort NetId { get; set; }
}
