using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Packets.Clientbound;

/// <summary>Result packet for the temporary client-adoption compatibility bridge.</summary>
public class ClientboundItemAdoptionPacket
{
    public ItemAdoptionResultData Result { get; set; }
}
