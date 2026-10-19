using UnityEngine;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Train;

public class ServerboundTrainRerailRequestPacket
{
    public ushort NetId { get; set; }
    public ushort TrackId { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Forward { get; set; }
}
