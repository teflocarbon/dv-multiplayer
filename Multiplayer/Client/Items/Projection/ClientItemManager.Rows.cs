using System;
using System.Collections;
using System.Collections.Generic;
using DV.Utils;
using Multiplayer.Client.Items.LiveState;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Utils;
using Multiplayer.Shared.Items.Inventory;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Client.Items.Projection;

internal sealed partial class ClientItemManager
{
    // A joiner's own inventory, as rows (docs/design/items/inventory.md, "Joining"; OI-COLD-03). The
    // server keeps these items as data only; this game makes the only objects, each from its row in
    // its exact slot, with its own saved state, under the NetId the server keeps it by. R4C's
    // take-out makes an object from a row the same way (MakeFromRow).

    /// <summary>Makes the joiner's inventory from the rows the server sent.</summary>
    internal void ReceiveInventoryRows(List<ItemUpdate> rows, string[] states)
    {
        if (rows == null || rows.Count == 0)
            return;
        CoroutineManager.Instance.StartCoroutine(MakeFromRows(rows, states ?? Array.Empty<string>()));
    }

    // As vanilla's starting items: every object made first, their state loaded a frame later, then
    // each goes into its slot by its row's Create.
    private IEnumerator MakeFromRows(List<ItemUpdate> rows, string[] states)
    {
        List<(NetworkedItem item, ItemUpdate row, string state)> made = new();
        for (int i = 0; i < rows.Count; i++)
        {
            NetworkedItem item = MakeFromRow(rows[i]);
            if (item != null)
                made.Add((item, rows[i], i < states.Length ? states[i] : string.Empty));
        }

        yield return null;

        int placed = 0;
        foreach ((NetworkedItem item, ItemUpdate row, string state) in made)
        {
            if (item == null)
                continue;
            try
            {
                ItemStateText.Load(item.gameObject, state);
                item.ReceiveSnapshot(row);
                placed++;
            }
            catch (Exception exception)
            {
                Multiplayer.LogException($"Inventory: {row.PrefabName} (netId {row.ItemNetId}) from its row failed", exception);
            }
        }
        Multiplayer.Log($"Inventory: made {placed} of {rows.Count} item(s) of this player's inventory from their rows");
    }

    /// <summary>
    /// An item's object, made from its row under its NetId at the safety position, bound to the
    /// server's item but not yet placed (its row's Create places it). Null if its prefab is unknown.
    /// </summary>
    internal NetworkedItem MakeFromRow(ItemUpdate row)
    {
        if (row == null || row.ItemNetId == 0)
            return null;
        if (NetworkedItem.TryGet(row.ItemNetId, out NetworkedItem existing) && existing != null)
            RetireClientProjection(existing, "inventory-row-replaced-existing-projection");
        if (!shell.TryGetItemPrefab(row.PrefabName, out GameObject prefab))
        {
            Multiplayer.LogError($"Inventory: no prefab called {row.PrefabName} for netId {row.ItemNetId}");
            return null;
        }
        GameObject gameObject = Object.Instantiate(prefab,
            StartingItemsController.ITEM_INSTANTIATION_SAFETY_POSITION, Quaternion.identity);
        NetworkedItem item = gameObject.GetOrAddComponent<NetworkedItem>();
        item.NetId = row.ItemNetId;
        item.Client()?.SetNetworkBinding(true);
        item.gameObject.SetActive(true);
        NetworkedItemManager.TraceItem("item.made-from-row", item, new() { ["prefabName"] = row.PrefabName });
        return item;
    }
}
