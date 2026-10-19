using Multiplayer.Shared.Protocol.Data.Train;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Train;

public class ClientboundTrainsetPhysicsPacket
{
    public int FirstNetId { get; set; }
    public int LastNetId { get; set; } 
    public uint Tick { get; set; }
    public TrainsetMovementPart[] TrainsetParts { get; set; }
}
