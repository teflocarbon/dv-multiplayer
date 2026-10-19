using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

/// <summary>TEMPORARY compatibility packet; not a general client-authority path.</summary>
public class ServerboundItemAdoptionPacket
{
    public ItemAdoptionRequestData Request { get; set; }
}
