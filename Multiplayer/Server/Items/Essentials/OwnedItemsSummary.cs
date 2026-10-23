using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Protocol.Data.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Server.Items.Essentials;

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

    public static List<OwnedItem> For(ServerPlayer player) =>
        player == null ? new List<OwnedItem>() : For(player.Guid, player.PlayerId);

    /// <param name="guid">The player's identity.</param>
    /// <param name="playerId">Their player number this session, for records without an owner identity.</param>
    public static List<OwnedItem> For(Guid guid, byte playerId)
    {
        List<OwnedItem> owned = new();

        // Live records: hands, inventory, the world, trains, containers' live items and Lost and Found,
        // including saved items still waiting for this owner to connect.
        foreach (AuthoritativeItemRegistry.Record record in AuthoritativeItemRegistry.GetAll())
        {
            if (record.Placement == ItemPlacementKind.Destroyed || string.IsNullOrEmpty(record.PrefabName))
                continue;
            // Player numbers are handed out per session, and a record restored from the save keeps
            // the number its owner had then: another player can have it now. That counted one
            // player's radio as another's, so DV wouldn't make them one and /give refused it. The
            // number is used only for a record without an owner identity.
            bool mine = record.PersistentOwnerIdentity != Guid.Empty
                ? record.PersistentOwnerIdentity == guid
                : record.PersistentOwnerPlayerId != 0 && record.PersistentOwnerPlayerId == playerId;
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
