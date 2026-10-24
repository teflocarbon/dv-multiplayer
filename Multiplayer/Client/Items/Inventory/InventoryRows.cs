using System.Collections.Generic;
using System.Linq;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Items.Inventory;

/// <summary>
/// The local player's inventory as the server's rows (docs/design/items/inventory.md, "Rows on
/// the client"; D14, OI-COLD-09): one row per item, by the NetId the server keeps it by, with
/// its update (prefab, exact slot, claim, relation, revision) and its own saved state
/// (<c>ItemSaveData</c> JSON). The server sends whole rows at the join and for every item it puts
/// into the inventory itself (<c>ClientboundInventoryRowsPacket</c>); every other change to a row
/// is that item's ordinary update, and a row leaves when an update takes the item anywhere else.
/// </summary>
internal static class InventoryRows
{
    internal sealed class Row
    {
        public ushort NetId;
        public string PrefabName = string.Empty;
        /// <summary>The latest update for the item, as the server built it for this player.</summary>
        public ItemUpdate Update;
        /// <summary>Its <c>ItemSaveData</c> JSON, or empty when it has none.</summary>
        public string State = string.Empty;

        public int Slot => Update?.InventorySlot ?? -1;
        public uint Revision => Update?.AuthorityRevision ?? 0;
    }

    private static readonly Dictionary<ushort, Row> rows = new();

    public static int Count => rows.Count;

    public static IEnumerable<Row> All() => rows.Values;

    public static bool TryGet(ushort netId, out Row row) => rows.TryGetValue(netId, out row);

    public static Row At(int slot) => slot < 0 ? null : rows.Values.FirstOrDefault(row => row.Slot == slot);

    /// <summary>Whether an update puts the item in the local player's inventory.</summary>
    public static bool IsLocalInventory(ItemUpdate update) =>
        update != null && update.UpdateType != ItemUpdate.ItemUpdateType.Destroy &&
        update.ItemState == ItemState.InInventory && update.PlayerId != 0 &&
        update.PlayerId == NetworkRole.LocalPlayerId;

    /// <summary>
    /// Whether a row sent after the join (a recall or Lost and Found retrieval into a slot, with its
    /// state) is no news here: its item has an object here and no stand-in (kept live in its slot, or
    /// taken out since: its own change decides), or its stand-in is past the row's revision (moved
    /// since).
    /// </summary>
    public static bool IsLate(ItemUpdate row) =>
        InventoryStandIn.TryGet(row.ItemNetId, out InventoryStandIn standIn) && !InventorySilhouettes.IsSilhouette(standIn)
            ? row.AuthorityRevision < standIn.Revision
            : NetworkedItem.TryGet(row.ItemNetId, out NetworkedItem item) && item != null;

    /// <summary>
    /// The server's rows: each one replaces what was kept for its item. <paramref name="full"/>
    /// is the whole inventory (the join): any other row is gone.
    /// </summary>
    public static void Receive(List<ItemUpdate> updates, string[] states, bool full)
    {
        if (full)
            rows.Clear();
        if (updates == null)
            return;
        for (int i = 0; i < updates.Count; i++)
        {
            ItemUpdate update = updates[i];
            if (update == null || update.ItemNetId == 0 || !full && IsLate(update))
                continue;
            rows[update.ItemNetId] = new Row
            {
                NetId = update.ItemNetId,
                PrefabName = update.PrefabName ?? string.Empty,
                Update = update,
                State = states != null && i < states.Length ? states[i] ?? string.Empty : string.Empty
            };
        }
    }

    /// <summary>
    /// An item's update: a row it keeps in the inventory follows it (its slot, claim and
    /// revision); one that takes it elsewhere, or a Destroy, ends the row. A Create into the
    /// inventory starts one, its state empty until a row brings it.
    /// </summary>
    public static void Observe(ItemUpdate update)
    {
        if (update == null || update.ItemNetId == 0)
            return;
        // An object's state alone doesn't say where the item is.
        if (update.UpdateType != ItemUpdate.ItemUpdateType.Destroy && !ItemUpdate.IncludesItemState(update.UpdateType))
            return;
        if (!IsLocalInventory(update))
        {
            rows.Remove(update.ItemNetId);
            return;
        }
        if (rows.TryGetValue(update.ItemNetId, out Row row))
        {
            if (update.AuthorityRevision < row.Revision)
                return;
            row.Update = update;
            if (!string.IsNullOrEmpty(update.PrefabName))
                row.PrefabName = update.PrefabName;
            return;
        }
        if (update.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create) || !string.IsNullOrEmpty(update.PrefabName))
            rows[update.ItemNetId] = new Row { NetId = update.ItemNetId, PrefabName = update.PrefabName ?? string.Empty, Update = update };
    }

    /// <summary>Keeps a row the local game made (a put-away), with the state it sent.</summary>
    public static Row Put(ushort netId, string prefabName, ItemUpdate update, string state)
    {
        Row row = new() { NetId = netId, PrefabName = prefabName ?? string.Empty, Update = update, State = state ?? string.Empty };
        rows[netId] = row;
        return row;
    }

    public static bool Remove(ushort netId) => rows.Remove(netId);

    public static void Clear() => rows.Clear();
}
