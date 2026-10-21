namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

/// <summary>An owner retrieves an item from their owned-items list, by its row's handle (owned-items.md, "Retrieval").</summary>
public class ServerboundOwnedItemRetrievePacket
{
    public uint RequestId { get; set; }
    public uint Handle { get; set; }
}
