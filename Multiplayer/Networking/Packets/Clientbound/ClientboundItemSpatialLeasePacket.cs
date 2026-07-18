using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundItemSpatialLeasePacket
{
    public bool Active { get; set; }
    public string Reason { get; set; }
    public ItemSpatialStateData State { get; set; }
}
