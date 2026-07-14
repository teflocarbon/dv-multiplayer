using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundItemAdoptionPacket
{
    public ItemAdoptionResultData Result { get; set; }
}
