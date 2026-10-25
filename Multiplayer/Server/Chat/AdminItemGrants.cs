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
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Essentials;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using UnityEngine;
using ItemMoveReason = Multiplayer.Core.Items.ChangeCauses.ItemMoveReason;

namespace Multiplayer.Server.Chat;

/// <summary>
/// Items an admin gives a player (/give, /restore in ChatManager), for one who lost theirs, e.g. to
/// a crash. Each item goes straight into the player's inventory as a row, owned by them, the way a saved
/// inventory comes back at login, so it's theirs to recall and find in Lost and Found. With their
/// inventory full it goes to their Lost and Found (InventoryOverflow), as a starting item does; only
/// if that's refused is it left at their feet.
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
        new(ServerLedger.Ledger.LiveOwnedBy(OwnerRef.Player(player?.Guid ?? Guid.Empty)).Select(entry => entry.Prefab),
            StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Gives the items, owned by them, half a second apart: each into a free slot as a row (D14),
    /// or, once their inventory is full, into their Lost and Found (at their feet if it refuses).
    /// </summary>
    public static IEnumerator Spawn(ServerPlayer player, IReadOnlyList<InventoryItemSpec> specs)
    {
        foreach (InventoryItemSpec spec in specs)
        {
            if (player == null || player.Peer == null)
                yield break;
            bool full = false;
            if (player.Guid != Guid.Empty && GiveAsRow(player, spec, out full))
            {
                yield return new WaitForSeconds(0.5f);
                continue;
            }
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
            // Owned by its target, born Granted (OI-ID-13).
            string refused = ServerLedger.Own(record, player.Guid, BirthReason.Granted, Guid.Empty, "by an admin command");
            if (refused.Length > 0)
                Multiplayer.LogWarning($"Couldn't make {player.Username} the owner of {spec.ItemPrefabName}: {refused}");
            if (record != null)
                ItemChangeLog.RecordMove(networked, record, ItemChangeLog.Move(ItemMoveReason.Granted, player,
                    "at their feet, owner " + player.Username));
            // Theirs with no slot free: into their Lost and Found, as a starting item made with the
            // inventory full is; if it refuses, it stays at their feet, in sight either way. A row refused
            // for another reason leaves it at their feet, its reason logged by the row.
            if (full && refused.Length == 0 && record != null &&
                LostAndFoundServer.Collect(networked, LostItemReason.InventoryOverflow, "given with the inventory full"))
                Multiplayer.Log($"Gave {player.Username} a {spec.ItemPrefabName} (item {networked.NetId}): their inventory is full, so it is in their Lost and Found");
            else
                Multiplayer.Log($"Gave {player.Username} a {spec.ItemPrefabName} (item {networked.NetId}), at their feet");
            yield return new WaitForSeconds(0.5f);
        }
    }

    // Into a free slot as a row, owned by them and born Granted (OI-ID-13), with no object made:
    // their game draws its stand-in, as a saved inventory comes back at login. False when their
    // inventory is full (<paramref name="full"/>) or the row was refused.
    private static bool GiveAsRow(ServerPlayer player, InventoryItemSpec spec, out bool full)
    {
        Guid id = Guid.NewGuid();
        int slot = ServerLedger.FreeSlot(id, player.Guid, -1, player.Guid);
        full = slot < 0;
        if (full)
            return false;
        SavedItemRow row = new() { PersistentItemId = id, PrefabName = spec.ItemPrefabName, OwnerIdentity = player.Guid };
        LiveItems.LiveItem record = PlayerItems.Issue(player, row, slot,
            ItemChangeLog.Move(ItemMoveReason.Granted, player, "by an admin command, slot " + slot),
            made => ServerLedger.Own(made, player.Guid, BirthReason.Granted, Guid.Empty, "by an admin command"));
        if (record == null)
        {
            Multiplayer.LogWarning($"Couldn't give {player.Username} a {spec.ItemPrefabName} as a row in slot {slot}; it goes to their feet");
            return false;
        }
        Multiplayer.Log($"Gave {player.Username} a {spec.ItemPrefabName} (item {record.NetId}), in their inventory, slot {record.Slot}");
        return true;
    }
}
