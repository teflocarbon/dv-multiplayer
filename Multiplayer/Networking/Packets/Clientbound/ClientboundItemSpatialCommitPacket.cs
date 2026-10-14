using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundItemSpatialCommitPacket
{
    public ItemSpatialStateData State { get; set; }
}
