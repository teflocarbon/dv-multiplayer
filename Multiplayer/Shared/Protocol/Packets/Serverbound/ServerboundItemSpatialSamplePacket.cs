using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

public class ServerboundItemSpatialSamplePacket
{
    public ItemSpatialStateData State { get; set; }
}
