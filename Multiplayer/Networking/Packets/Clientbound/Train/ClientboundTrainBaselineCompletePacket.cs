namespace Multiplayer.Networking.Packets.Clientbound.Train;

public sealed class ClientboundTrainBaselineCompletePacket
{
    public uint BaselineId { get; set; }
    public uint TrainsetCount { get; set; }
    public uint CarCount { get; set; }
}
