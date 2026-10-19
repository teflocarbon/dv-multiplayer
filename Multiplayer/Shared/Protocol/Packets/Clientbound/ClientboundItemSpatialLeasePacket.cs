using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound;

public class ClientboundItemSpatialLeasePacket
{
    public bool Active { get; set; }
    public string Reason { get; set; }
    public ItemSpatialStateData State { get; set; }
}
