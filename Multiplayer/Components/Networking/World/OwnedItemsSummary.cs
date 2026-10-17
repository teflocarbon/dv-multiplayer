using Multiplayer.Components.Networking.World.Containers;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Data;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Components.Networking.World;

/// <summary>
/// Everything a player owns anywhere the host keeps items, by prefab, for their join.
///
/// While loading, each game's DV adds any starting items (comms radio, maps, compass, wallet, ...)
/// and licence papers missing from the items it was given (StartingItemsController's
/// StartingItemsSafeguard and LicensesSafeguard). A client is given only its starter kit, so it
/// used to make a fresh set on every join, on top of the ones the host then handed back: the
/// duplicates. The host now sends what the player already owns, and the client counts it as
/// present (StartingItemsControllerPatch), so DV adds only what is really missing, as in single player.
/// </summary>
public static class OwnedItemsSummary
{
    public readonly struct OwnedItem
    {
        public readonly string Prefab;
        /// <summary>The inventory slot it holds, or -1.</summary>
        public readonly int Slot;

        public OwnedItem(string prefab, int slot)
        {
            Prefab = prefab;
            Slot = slot;
        }
    }

    public static List<OwnedItem> For(ServerPlayer player)
    {
        List<OwnedItem> owned = new();
        if (player == null)
            return owned;
        Guid guid = player.Guid;

        // Live records: hands, inventory, the world, trains, containers' live items and Lost and Found,
        // including saved items still waiting for this owner to connect.
        foreach (AuthoritativeItemRegistry.Record record in AuthoritativeItemRegistry.GetAll())
        {
            if (record.Placement == ItemPlacementKind.Destroyed || string.IsNullOrEmpty(record.PrefabName))
                continue;
            bool mine = record.PersistentOwnerIdentity == guid ||
                        (record.PersistentOwnerPlayerId != 0 && record.PersistentOwnerPlayerId == player.PlayerId);
            if (!mine)
                continue;
            int slot = record.Placement == ItemPlacementKind.PlayerInventory ? record.InventoryClaimSlot : -1;
            owned.Add(new OwnedItem(record.PrefabName, slot));
        }

        // Saved Lost and Found items not restored yet.
        foreach (string prefab in NetworkedLostAndFoundManager.PendingPrefabsOwnedBy(guid))
            owned.Add(new OwnedItem(prefab, -1));

        // Items stored in cold containers.
        string identity = guid.ToString("D");
        foreach (var item in NetworkedColdContainerManager.Graph.ExportSnapshot().Items)
            if (string.Equals(item.PersistentOwnerIdentity, identity, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(item.PrefabName))
                owned.Add(new OwnedItem(item.PrefabName, -1));

        return owned;
    }
}
