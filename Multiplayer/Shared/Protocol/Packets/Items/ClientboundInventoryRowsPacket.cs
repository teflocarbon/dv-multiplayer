using System.Collections.Generic;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Items;

/// <summary>
/// Rows of the recipient's own inventory (docs/design/items/inventory.md, "Rows on the client";
/// OI-COLD-03, D14): each item's Create, built for them (NetId, exact slot, claim, relation,
/// revision), and beside it the item's own saved state (its <c>ItemSaveData</c> JSON, or empty).
/// Sent whole at the join (<see cref="Full"/>) and for each item the server puts into the
/// inventory itself; the server keeps them as data only.
/// </summary>
public class ClientboundInventoryRowsPacket
{
    /// <summary>The whole inventory: any row the recipient keeps that isn't here is gone.</summary>
    public bool Full { get; set; }
    public List<ItemUpdate> Rows { get; set; }
    /// <summary>Each row's saved state, by index; empty when the item has none.</summary>
    public string[] States { get; set; }
}
