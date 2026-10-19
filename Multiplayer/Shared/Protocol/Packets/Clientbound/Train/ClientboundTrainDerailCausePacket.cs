using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Train;

/// <summary>Why a car derailed on the host. The derail itself reaches clients with the car's physics.</summary>
public class ClientboundTrainDerailCausePacket
{
    public ushort NetId { get; set; }
    public ChangeCause<DerailReason> Cause { get; set; }
}
