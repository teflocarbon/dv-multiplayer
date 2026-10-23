using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV;
using DV.CabControls;
using DV.InventorySystem;
using DV.Utils;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Essentials;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using UnityEngine;
using ItemMoveReason = Multiplayer.Core.Items.ChangeCauses.ItemMoveReason;

namespace Multiplayer.Server.Chat;

/// <summary>
/// Items an admin gives a player (/give, /restore in ChatManager), for one who lost theirs, e.g. to
/// a crash. Each item goes straight into the player's inventory, owned by them, the way a saved
/// inventory comes back at login, so it's theirs to recall and find in Lost and Found. Only if
/// that's refused is it left at their feet.
///
/// Essentials (comms radio, maps, wallet, ...) are one per player in DV, so /give refuses an
/// essential the player already owns anywhere; /restore only gives starting items they own nowhere.
/// </summary>
internal static class AdminItemGrants
{
    public const int MaxCount = 20;

    /// <summary>An item by its prefab name, ignoring case. Null, with near names, when there's none.</summary>
    public static InventoryItemSpec Find(string name, out string[] nearNames)
    {
        nearNames = Array.Empty<string>();
        var all = Globals.G?.Items?.items?.Where(spec => spec != null && !string.IsNullOrEmpty(spec.ItemPrefabName)).ToList();
        if (all == null || string.IsNullOrWhiteSpace(name))
            return null;
        InventoryItemSpec exact = all.FirstOrDefault(spec => string.Equals(spec.ItemPrefabName, name, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;
        nearNames = all.Select(spec => spec.ItemPrefabName)
            .Where(prefab => prefab.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             name.IndexOf(prefab, StringComparison.OrdinalIgnoreCase) >= 0)
            .Distinct().OrderBy(prefab => prefab.Length).Take(8).ToArray();
        return null;
    }

    /// <summary>Whether items can be given now. They go into the inventory, so anywhere will do.</summary>
    public static bool CanReceive(ServerPlayer player, out string reason)
    {
        reason = string.Empty;
        return player != null && player.Peer != null;
    }

    /// <summary>The prefab names of everything the player owns, wherever it is.</summary>
    public static HashSet<string> OwnedPrefabs(ServerPlayer player) =>
        new(OwnedItemsSummary.For(player).Select(item => item.Prefab), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// DV's starting items for this career (its difficulty decides them), by prefab name: what
    /// every player should own.
    /// </summary>
    public static List<string> StartingItemPrefabs()
    {
        List<string> prefabs = new();
        StartingItemsController controller = StartingItemsController.Instance;
        MethodInfo get = typeof(StartingItemsController).GetMethod("GetStartingItemsPrefabs",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (controller == null || get == null || SaveGameManager.Instance?.data == null)
            return prefabs;
        if (get.Invoke(controller, new object[] { SaveGameManager.Instance.data }) is not IEnumerable items)
            return prefabs;
        foreach (object item in items)
        {
            string prefab = item?.GetType().GetProperty("ItemPrefabName")?.GetValue(item) as string ??
                            item?.GetType().GetField("ItemPrefabName")?.GetValue(item) as string;
            if (!string.IsNullOrEmpty(prefab))
                prefabs.Add(prefab);
        }
        return prefabs;
    }

    /// <summary>Spawns the items at the player's feet, owned by them, half a second apart.</summary>
    public static IEnumerator Spawn(ServerPlayer player, IReadOnlyList<InventoryItemSpec> specs)
    {
        foreach (InventoryItemSpec spec in specs)
        {
            if (player == null || player.Peer == null)
                yield break;
            // A little in front of them, off the ground, so it settles beside them.
            Quaternion facing = Quaternion.Euler(0f, player.WorldRotationY, 0f);
            Vector3 at = player.WorldPosition + facing * new Vector3(0f, 0.6f, 0.8f);
            GameObject obj = UnityEngine.Object.Instantiate(spec.gameObject, at, facing, WorldMover.OriginShiftParent);
            InventoryItemSpec placed = obj.GetComponent<InventoryItemSpec>();
            if (placed != null)
                placed.BelongsToPlayer = true;
            ItemBase item = obj.GetComponent<ItemBase>();
            if (item != null)
            {
                StorageController.Instance.AddItemToWorldStorage(item);
                if (item.ItemRigidbody != null)
                {
                    item.ItemRigidbody.velocity = Vector3.zero;
                    item.ItemRigidbody.angularVelocity = Vector3.zero;
                }
            }
            NetworkedItem networked = obj.GetComponent<NetworkedItem>() ?? obj.AddComponent<NetworkedItem>();
            yield return null; // its net id is assigned as it starts
            if (networked == null || networked.NetId == 0 || player.Guid == Guid.Empty)
            {
                Multiplayer.LogWarning($"Couldn't give {player.Username} a {spec.ItemPrefabName}: it has no net id");
                continue;
            }
            LiveItems.LiveItem record = LiveItems.Ensure(networked);
            if (record != null && record.Owner == Guid.Empty)
                record.Owner = player.Guid;
            bool inInventory = PutInInventory(player, networked, record);
            if (!inInventory && record != null)
                ItemChangeLog.RecordMove(networked, record, ItemChangeLog.Move(ItemMoveReason.Granted, player,
                    "at their feet, owner " + player.Username));
            Multiplayer.Log($"Gave {player.Username} a {spec.ItemPrefabName} (item {networked.NetId}), " +
                            (inInventory ? "in their inventory" : "at their feet"));
            yield return new WaitForSeconds(0.5f);
        }
    }

    // As a saved inventory item comes back at the owner's login (WorldItemPersistenceRestore): the
    // host makes it "in this player's inventory", and their game puts it in a free slot.
    private static bool PutInInventory(ServerPlayer player, NetworkedItem item, LiveItems.LiveItem record)
    {
        if (record == null)
            return false;
        ItemUpdate snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.FullSync);
        if (snapshot == null)
            return false;
        snapshot.ItemState = ItemState.InInventory;
        snapshot.PlayerId = player.PlayerId;
        snapshot.PlayerHand = PlayerHand.None;
        snapshot.AuthorityRevision = record.Revision;
        if (!LiveItems.TryApplyTransition(item, snapshot, player,
                ItemTransitionReason.HostLocalState, true, out string rejection,
                cause: ItemChangeLog.Move(ItemMoveReason.Granted, player, "by an admin command")))
        {
            Multiplayer.LogWarning($"Couldn't put {item.name} in {player.Username}'s inventory ({rejection}); leaving it at their feet");
            return false;
        }
        LiveItems.WriteToSnapshot(record, snapshot, ItemTransitionReason.HostLocalState);
        item.ApplyServerCanonicalSnapshot(snapshot);
        ServerItemManager.Instance?.RegisterHostWorldItem(item);
        NetworkServer.Instance.SendItemUpdate(snapshot);
        return true;
    }
}
