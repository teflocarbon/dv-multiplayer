using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Data.Train;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

public class ClientboundCustomizationsPacket
{
    public CustomizationHoleData[] Holes { get; set; }
}
