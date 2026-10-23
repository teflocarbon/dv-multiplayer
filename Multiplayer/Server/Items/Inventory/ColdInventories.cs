using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Shared.Protocol.Data.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Server.Items.Inventory;

/// <summary>
/// Cold inventories (docs/design/items/inventory.md, "Cold inventories"; D12, OI-COLD-01): the items of
/// players who aren't here, kept as data only. Each is one saved row, its state and its exact slot,
/// with no object on any game; the ledger keeps an owned one's entry, its home still
/// <c>Slot(holder, slot)</c>. A row goes live again when its holder joins (<see cref="PlayerItems.Join"/>).
/// </summary>
internal static class ColdInventories
{
    private static readonly Dictionary<Guid, SavedItemRow> rows = new();

    public static int Count => rows.Count;

    public static IEnumerable<SavedItemRow> All() => rows.Values;

    /// <summary>A holder's rows, in slot order.</summary>
    public static IReadOnlyList<SavedItemRow> Of(Guid holder) =>
        rows.Values.Where(row => row.PlacementPlayerIdentity == holder).OrderBy(row => row.Slot).ToList();

    /// <summary>Whether any row is held or owned by this identity.</summary>
    public static bool HasIdentity(Guid identity) => identity != Guid.Empty &&
        rows.Values.Any(row => row.PlacementPlayerIdentity == identity || row.OwnerIdentity == identity);

    public static bool Contains(Guid id) => id != Guid.Empty && rows.ContainsKey(id);

    public static bool TryGet(Guid id, out SavedItemRow row) => rows.TryGetValue(id, out row);

    /// <summary>The row in a holder's slot, or null.</summary>
    public static SavedItemRow At(Guid holder, int slot) => slot < 0 ? null :
        rows.Values.FirstOrDefault(row => row.PlacementPlayerIdentity == holder && row.Slot == slot);

    /// <summary>Keeps a row. False for one without an id or a holder, or one already kept.</summary>
    public static bool Add(SavedItemRow row)
    {
        if (row == null || row.PersistentItemId == Guid.Empty || row.PlacementPlayerIdentity == Guid.Empty ||
            rows.ContainsKey(row.PersistentItemId))
            return false;
        row.Placement = ItemPlacementKind.PlayerInventory;
        rows.Add(row.PersistentItemId, row);
        return true;
    }

    public static bool Remove(Guid id) => rows.Remove(id);

    /// <summary>
    /// A re-keyed player's rows follow them to their new identity, merged with its rows (OI-ID-19).
    /// A row whose slot the new identity already uses, or that the ledger sent to Lost and Found
    /// (<paramref name="lost"/>), loses its slot: at join it goes to a free one or to Lost and Found.
    /// </summary>
    public static int Rekey(Guid from, Guid to, ICollection<Guid> lost = null)
    {
        HashSet<int> taken = new(rows.Values.Where(row => row.PlacementPlayerIdentity == to && row.Slot >= 0)
            .Select(row => row.Slot));
        int moved = 0;
        foreach (SavedItemRow row in rows.Values)
        {
            if (row.OwnerIdentity == from)
                row.OwnerIdentity = to;
            if (row.PlacementPlayerIdentity != from)
                continue;
            row.PlacementPlayerIdentity = to;
            if (row.Slot >= 0 && (lost?.Contains(row.PersistentItemId) == true || !taken.Add(row.Slot)))
                row.Slot = -1;
            moved++;
        }
        return moved;
    }

    public static void Clear() => rows.Clear();
}
