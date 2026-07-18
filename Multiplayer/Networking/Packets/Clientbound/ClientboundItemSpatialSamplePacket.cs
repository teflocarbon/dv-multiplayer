using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundItemSpatialSamplePacket
{
    public ItemSpatialStateData State { get; set; }
}
