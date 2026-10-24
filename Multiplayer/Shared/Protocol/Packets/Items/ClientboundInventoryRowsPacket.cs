using System.Collections.Generic;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Items;

/// <summary>
/// A joiner's own inventory, as rows (docs/design/items/inventory.md, "Joining"; OI-COLD-03): each
/// item's Create, built for the joiner (NetId, exact slot, claim, relation, revision), and beside it
/// the item's own saved state (its <c>ItemSaveData</c> JSON, or empty). The joiner's game makes each
/// object from its row in its slot; the server keeps them as data only.
/// </summary>
public class ClientboundInventoryRowsPacket
{
    public List<ItemUpdate> Rows { get; set; }
    /// <summary>Each row's saved state, by index; empty when the item has none.</summary>
    public string[] States { get; set; }
}
