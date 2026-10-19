using Multiplayer.Shared.Protocol.Data.Train;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Train;

public class ClientboundTrainsetPhysicsPacket
{
    public int FirstNetId { get; set; }
    public int LastNetId { get; set; } 
    public uint Tick { get; set; }
    /// <summary>
    /// Ticks until this player's next sample of the set while it moves: the host sends a set to a
    /// player less often the further away they are (Server/Trains/NetworkTrainsetWatcher.cs).
    /// 0 or 1 is every tick.
    /// </summary>
    public byte IntervalTicks { get; set; }
    public TrainsetMovementPart[] TrainsetParts { get; set; }
}
