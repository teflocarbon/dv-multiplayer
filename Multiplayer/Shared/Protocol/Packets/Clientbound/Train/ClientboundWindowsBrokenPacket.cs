using UnityEngine;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Train;

public class ClientboundWindowsBrokenPacket
{
    public ushort NetId { get; set; }
    public Vector3 ForceDirection { get; set; }
}
