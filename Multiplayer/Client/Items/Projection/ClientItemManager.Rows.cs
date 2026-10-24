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
    // server keeps these items as data only, and so does this game (D14): each row is a stand-in in
    // its exact slot, with its own saved state, under the NetId the server keeps it by. No object is
    // made until the item leaves its slot (MakeFromRow).

    /// <summary>Fills the joiner's inventory with stand-ins for the rows the server sent.</summary>
    internal void ReceiveInventoryRows(List<ItemUpdate> rows, string[] states)
    {
        if (rows == null || rows.Count == 0)
            return;
        CoroutineManager.Instance.StartCoroutine(FillFromRows(rows, states ?? Array.Empty<string>()));
    }

    private IEnumerator FillFromRows(List<ItemUpdate> rows, string[] states)
    {
        // The rows can come before the player's inventory exists.
        while (DV.InventorySystem.Inventory.Instance == null)
            yield return null;
        int placed = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            ItemUpdate row = rows[i];
            try
            {
                if (ReceiveAsStandIn(row, i < states.Length ? states[i] : string.Empty))
                    placed++;
                else if (row != null)
                    ProcessReceivedAsClient(row);
            }
            catch (Exception exception)
            {
                Multiplayer.LogException($"Inventory: {row?.PrefabName} (netId {row?.ItemNetId}) from its row failed", exception);
            }
        }
        Multiplayer.Log($"Inventory: {placed} of {rows.Count} item(s) of this player's inventory are stand-ins from their rows");
    }

    /// <summary>
    /// An item's object, made from its row under its NetId at the safety position and bound to the
    /// server's item, its own saved state loaded, but not yet placed (its update places it). Null if
    /// its prefab is unknown.
    /// </summary>
    internal NetworkedItem MakeFromRow(ItemUpdate row, string state = null)
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
        try
        {
            ItemStateText.Load(gameObject, state);
        }
        catch (Exception exception)
        {
            Multiplayer.LogWarning($"Inventory: {row.PrefabName} (netId {row.ItemNetId})'s saved state didn't load: {exception.Message}");
        }
        NetworkedItemManager.TraceItem("item.made-from-row", item, new() { ["prefabName"] = row.PrefabName });
        return item;
    }
}
