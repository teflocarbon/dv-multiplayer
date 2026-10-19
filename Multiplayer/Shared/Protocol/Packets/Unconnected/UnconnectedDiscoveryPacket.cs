using LiteNetLib.Utils;
using Multiplayer.Shared.Protocol.Data;

namespace Multiplayer.Shared.Protocol.Packets.Unconnected;

public class UnconnectedDiscoveryPacket
{
    public bool IsResponse { get; set; } = false;
    public LobbyServerData Data { get; set; }
}
