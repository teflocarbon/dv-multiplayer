namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Train;

public sealed class ClientboundTrainBaselineBeginPacket
{
    public uint BaselineId { get; set; }
    public uint TrainsetCount { get; set; }
    public uint CarCount { get; set; }
    public string Failure { get; set; }
}
