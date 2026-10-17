using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV;
using DV.CabControls;
using DV.InventorySystem;
using DV.Utils;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data;
using UnityEngine;

namespace Multiplayer.Networking.Managers.Server;

/// <summary>
/// Items an admin gives a player (/give, /restore in ChatManager), for one who lost theirs, e.g. to
/// a crash. Each item is spawned at the player's feet and owned by them, as a shop purchase is, so
/// it's theirs to pick up, recall and find in Lost and Found.
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

    /// <summary>
    /// Whether items can be put at the player's feet now. Not while they're on a train: an item
    /// spawned on a cab floor was never projected to anyone, so nobody could pick it up.
    /// </summary>
    public static bool CanReceive(ServerPlayer player, out string reason)
    {
        reason = string.Empty;
        if (player.CarId == 0)
            return true;
        reason = $"{player.Username} is on a train; ask them to step off onto the ground first, then try again.";
        return false;
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
            if (networked != null && networked.NetId != 0 && player.Guid != Guid.Empty &&
                AuthoritativeItemRegistry.Ensure(networked) is { } record && record.PersistentOwnerPlayerId == 0)
            {
                record.PersistentOwnerPlayerId = player.PlayerId;
                record.PersistentOwnerIdentity = player.Guid;
                player.AddOwnedItem(networked.NetId);
            }
            Multiplayer.Log($"Gave {player.Username} a {spec.ItemPrefabName} (item {networked?.NetId})");
            yield return new WaitForSeconds(0.5f);
        }
    }
}
