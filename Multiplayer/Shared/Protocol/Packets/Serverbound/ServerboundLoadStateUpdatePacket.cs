using Multiplayer.Shared.Protocol.Data;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound
{
    public class ServerboundLoadStateUpdatePacket
    {
        public PlayerLoadingState LoadState { get; set; }
    }
}
