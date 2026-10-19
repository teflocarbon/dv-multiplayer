namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Train;

public sealed class ServerboundTrainTopologySyncRequestPacket
{
    public uint KnownBaselineId { get; set; }
    public uint KnownTopologyRevision { get; set; }
}
