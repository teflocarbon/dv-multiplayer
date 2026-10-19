using Multiplayer.Shared.Protocol.Data.Train;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Train;

public class ClientboundCarHealthUpdatePacket
{
    public ushort NetId { get; set; }
    public TrainCarHealthData Health { get; set; }
}
