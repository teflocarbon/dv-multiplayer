using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound;

public class ClientboundItemSpatialSamplePacket
{
    public ItemSpatialStateData State { get; set; }
}
