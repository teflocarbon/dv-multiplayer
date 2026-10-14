using Multiplayer.Networking.Data.Train;
using System.Collections.Generic;

namespace Multiplayer.Networking.Packets.Clientbound.Train;

public class ClientboundSpawnTrainSetPacket
{
    // Zero identifies a live spawn. Non-zero values belong to an initial rolling-stock baseline.
    public uint BaselineId { get; set; }
    public uint Sequence { get; set; }
    public TrainsetSpawnPart[] SpawnParts { get; set; }
    public bool AutoCouple { get; set; }

    public static ClientboundSpawnTrainSetPacket FromTrainSet(List<TrainCar> trainset, bool autoCouple)
    {
        return new ClientboundSpawnTrainSetPacket {
            SpawnParts = TrainsetSpawnPart.FromTrainSet(trainset),
            AutoCouple = autoCouple
        };
    }
}
