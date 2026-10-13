namespace Multiplayer.Networking.Packets.Common.Train;

public class CommonHandbrakePositionPacket
{
    public ulong OperationId { get; set; }
    public uint ExpectedRevision { get; set; }
    public ushort NetId { get; set; }
    public float Position { get; set; }
}
