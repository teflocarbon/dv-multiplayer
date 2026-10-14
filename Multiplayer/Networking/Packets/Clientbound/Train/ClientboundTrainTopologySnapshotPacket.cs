using Multiplayer.Core.Trains;
using Multiplayer.Networking.Data.Train;

namespace Multiplayer.Networking.Packets.Clientbound.Train;

public sealed class ClientboundTrainTopologySnapshotPacket
{
    public uint BaselineId { get; set; }
    public uint TopologyRevision { get; set; }
    public ulong OperationId { get; set; }
    public TrainTopologyResultCode Result { get; set; }
    public TrainEndpointStateData[] Endpoints { get; set; }
    public BrakeLineStateData[] BrakeLines { get; set; }
    public TrainControlRevisionData[] ControlRevisions { get; set; }
    public TrainControlStateData[] ControlStates { get; set; }
}
