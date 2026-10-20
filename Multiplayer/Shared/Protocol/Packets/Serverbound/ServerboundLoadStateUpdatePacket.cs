using Multiplayer.Shared.Protocol.Data;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound
{
    public class ServerboundLoadStateUpdatePacket
    {
        public PlayerLoadingState LoadState { get; set; }

        // The client's TrackGraphFingerprint, sent with ReadyForWorldState (zero with every other
        // state). The host refuses the join if it differs from its own.
        public ulong TrackGraphHash { get; set; }
        public ushort TrackCount { get; set; }
        public ushort JunctionCount { get; set; }
        public ushort TurntableCount { get; set; }

        // The client's ShopCatalogueFingerprint, sent the same way: the host refuses the join if
        // its shops differ.
        public ulong ShopCatalogueHash { get; set; }
        public ushort ShopCount { get; set; }
        public ushort ShopItemCount { get; set; }
    }
}
