namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

/// <summary>A player asks for their owned-items list (owned-items.md, "The owned-items list").</summary>
public class ServerboundOwnedItemsRequestPacket
{
    public uint RequestId { get; set; }
}
