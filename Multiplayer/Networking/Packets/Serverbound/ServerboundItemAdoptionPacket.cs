using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Packets.Serverbound;

/// <summary>TEMPORARY compatibility packet; not a general client-authority path.</summary>
public class ServerboundItemAdoptionPacket
{
    public ItemAdoptionRequestData Request { get; set; }
}
