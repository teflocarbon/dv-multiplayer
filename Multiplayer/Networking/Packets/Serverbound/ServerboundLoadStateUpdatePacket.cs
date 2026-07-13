using Multiplayer.Networking.Data;

namespace Multiplayer.Networking.Packets.Serverbound
{
    public class ServerboundLoadStateUpdatePacket
    {
        public PlayerLoadingState LoadState { get; set; }
    }
}
