using DV;
using UnityEngine;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Shared.Items.LiveState;

public partial class NetworkedItemManager
{
    // Prefab lookup and recall requests. A remote client's dynamic projections, unbound items and
    // temporary compatibility adoption are its part's (ClientItemManager.Compatibility.cs).

    private void BuildPrefabLookup()
    {
        (NetworkLifecycle.Instance.LocalSession as NetworkManager)?.LogDebug(() => $"BuildPrefabLookup()");

        foreach (var item in Globals.G.Items.items)
        {
            if (!ItemPrefabs.ContainsKey(item.ItemPrefabName))
            {
                ItemPrefabs[item.itemPrefabName] = item;
            }
        }
    }

    internal bool TryGetItemPrefab(string prefabName, out GameObject prefab)
    {
        prefab = null;
        if (string.IsNullOrWhiteSpace(prefabName) ||
            !ItemPrefabs.TryGetValue(prefabName, out InventoryItemSpec spec) || spec?.gameObject == null)
            return false;
        prefab = spec.gameObject;
        return true;
    }

    public bool RequestItemRecall(NetworkedItem item, int requestedSlot) =>
        item != null && RequestItemRecall(item.NetId, item.AuthorityRevision, item.Relation, requestedSlot);

    /// <summary>A recall by the item's NetId: a silhouette with no object of its item here asks with its row's.</summary>
    public bool RequestItemRecall(ushort netId, uint expectedRevision, ItemRelation relation, int requestedSlot)
    {
        if (netId == 0)
            return false;
        byte localPlayerId = NetworkRole.LocalPlayerId;
        DebugRuntime.Publish("item", "item.recall-requested",
            NetworkRole.DebugSide,
            entityType: "Item", entityId: netId.ToString(), data: new()
            {
                ["requestingPlayerId"] = localPlayerId,
                ["requestedSlot"] = requestedSlot,
                ["expectedRevision"] = expectedRevision,
                ["relation"] = relation.ToString()
            });

        // One step, the host's player's too (docs/design/items/wire.md, "Recall"): the server
        // commits it and everyone gets the update.
        return ItemClient.Current?.SendItemRequest(new ItemRequest
        {
            ItemNetId = netId,
            ExpectedRevision = expectedRevision,
            Action = ItemAction.Recall,
            Slot = requestedSlot
        }) > 0;
    }
}
