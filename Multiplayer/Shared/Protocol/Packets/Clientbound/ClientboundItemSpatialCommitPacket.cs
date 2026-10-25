using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound;

public class ClientboundItemSpatialCommitPacket
{
    public ItemSpatialStateData State { get; set; }
    /// <summary>Why it moved, when the commit moved it somewhere new or the server put it back
    /// (Unknown: it came to rest where it was released). OI-SYNC-18.</summary>
    public ItemMoveReason MoveReason { get; set; }
    public byte MovedByPlayerId { get; set; }
    public string MoveDetail { get; set; } = string.Empty;
}
