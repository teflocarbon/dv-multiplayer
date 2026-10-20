using DV;
using UnityEngine;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Session;
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Shared.Items;

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

    public bool RequestItemRecall(NetworkedItem item, int requestedSlot)
    {
        if (item == null || item.NetId == 0)
            return false;
        byte localPlayerId = NetworkLifecycle.Instance.IsHost()
            ? ItemServer.Current?.LocalPlayerId ?? 0
            : NetworkLifecycle.Instance.LocalSession?.PlayerId ?? 0;
        DebugRuntime.Publish("item", "item.recall-requested",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "Item", entityId: item.NetId.ToString(), data: new()
            {
                ["requestingPlayerId"] = localPlayerId,
                ["requestedSlot"] = requestedSlot,
                ["expectedRevision"] = item.AuthorityRevision,
                ["persistentOwnerPlayerId"] = item.PersistentOwnerPlayerId
            });

        if (!NetworkLifecycle.Instance.IsHost())
        {
            ItemClient.Current?.SendItemRecall(item.NetId, item.AuthorityRevision, requestedSlot);
            return true;
        }

        return ServerPart?.RequestHostRecall(item, requestedSlot) == true;
    }
}
