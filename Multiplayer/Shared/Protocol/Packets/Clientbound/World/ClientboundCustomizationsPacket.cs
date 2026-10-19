using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

public class ClientboundCustomizationsPacket
{
    public CustomizationHoleData[] Holes { get; set; }
}
