using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Train;

public class ClientboundDestroyTrainCarPacket
{
    public ushort NetId { get; set; }
    public ChangeCause<CarRemovalReason> Cause { get; set; }
}
