using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Packets.Serverbound;

public class ServerboundItemSpatialSamplePacket
{
    public ItemSpatialStateData State { get; set; }
}
