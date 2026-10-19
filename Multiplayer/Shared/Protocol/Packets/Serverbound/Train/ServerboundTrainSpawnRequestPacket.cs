using UnityEngine;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Train;

public class ServerboundTrainSpawnRequestPacket
{
    public string LiveryId { get; set; }
    public ushort TrackNetId { get; set; }
    public int Index{ get; set; }
    public bool WithTrackDirection { get; set; }
}
