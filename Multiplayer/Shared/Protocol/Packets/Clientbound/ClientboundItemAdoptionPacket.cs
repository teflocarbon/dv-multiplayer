using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound;

/// <summary>Result packet for the temporary client-adoption compatibility bridge.</summary>
public class ClientboundItemAdoptionPacket
{
    public ItemAdoptionResultData Result { get; set; }
}
