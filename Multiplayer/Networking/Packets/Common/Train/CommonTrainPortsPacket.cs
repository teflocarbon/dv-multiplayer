namespace Multiplayer.Networking.Packets.Common.Train;

public class CommonTrainPortsPacket
{
    public ulong OperationId { get; set; }
    public uint ExpectedRevision { get; set; }
    public ushort NetId { get; set; }
    public uint[] PortIds { get; set; }
    public float[] PortValues { get; set; }
    /// <summary>The sender's tick when it read the values. The host's own (sent through its local
    /// client) stamp the commit that passes them on, for clients to play them out at.</summary>
    public uint Tick { get; set; }
}
