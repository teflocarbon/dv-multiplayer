using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Packets.Serverbound;

public class ServerboundItemSpatialSettlementPacket
{
    public ItemSpatialStateData State { get; set; }
}
