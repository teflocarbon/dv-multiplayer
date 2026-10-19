
using Multiplayer.Shared.Protocol.Data;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound;

public class ClientboundLoadStateInfoPacket
{

    public PlayerLoadingState LoadingState { get; set; }
    public uint ItemsToLoad { get; set; }

}
