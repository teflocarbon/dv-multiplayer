using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Packets.Serverbound;

public class ServerboundItemAdoptionPacket
{
    public ItemAdoptionRequestData Request { get; set; }
}
