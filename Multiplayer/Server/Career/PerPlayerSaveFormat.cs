using DV;
using DV.InventorySystem;
using DV.JObjectExtstensions;
using DV.ThingTypes;
using Multiplayer.Server.Items;
using Multiplayer.Server.Players;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Core.Containers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Career;

/// <summary>
/// The save format where every player's state is stored by their player ID (PlayerIdentity), the
/// host's own player included: a position under Multiplayer.Players.&lt;ID&gt;, and item records
/// owned by that ID
/// (WorldItemsV1). The server restores it at that player's login, wherever the server runs. See
/// docs/design/server-player-split.md.
///
/// DV's own fields (Player_position, Storage_Inventory) are still written from the local player,
/// so a career opens in vanilla DV without the mod, but the mod no longer reads them, except to
/// convert a career saved before this format, once.
///
/// Runs when hosting starts (SaveGameManager_FindStartGameData_Patch), after DV has read the save
/// and before it places the player or loads any item.
/// </summary>
public static class PerPlayerSaveFormat
{
    private const string RootKey = "Multiplayer";
    private const string PlayersKey = "Players";
    private const string FormatKey = "PlayerStateFormat";
    private const int Version = 2;

    /// <summary>
    /// What the local player already owns according to the save, for DV's starting-item and
    /// licence safeguards this load (StartingItemsControllerPatch). Null when not hosting.
    /// </summary>
    public static List<StorageItemData> LocalOwnedItems { get; private set; }

    /// <summary>The ID the local player logs in with: this game's Steam account's.</summary>
    private static Guid LocalId => PlayerIdentity.Local;

    /// <summary>
    /// Who a career saved before this format belongs to: the Steam account the game runs under, or
    /// -mp-owner, for a career copied to a server PC running under another account.
    /// </summary>
    private static Guid ConversionOwner => CareerOwner.Id;

    public static void PrepareLoad(SaveGameData data)
    {
        LocalOwnedItems = null;
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (data == null || !lifecycle.IsServerRunning)
            return;

        JObject root = data.GetJObject(RootKey) ?? new JObject();
        if (((int?)root[FormatKey] ?? 0) < Version)
            Convert(data, root, ConversionOwner);
        root[FormatKey] = Version;

        // The local player's state saved under the install's GUID, before player IDs were Steam
        // accounts, moves to their Steam ID once. Other players' moves at their login.
        Guid installGuid = Multiplayer.Settings.GetGuid();
        if (!lifecycle.IsDedicatedHost && installGuid != LocalId &&
            InstallGuidRekey.SaveOwns(root, installGuid) && !InstallGuidRekey.SaveOwns(root, LocalId))
            Multiplayer.Log($"Save format: moved {InstallGuidRekey.RekeySave(root, installGuid, LocalId)} record(s) " +
                            $"of the local player from install GUID {installGuid} to {PlayerIdentity.Describe(LocalId)}");
        data.SetJObject(RootKey, root);

        if (!lifecycle.IsDedicatedHost)
        {
            // The local player spawns where their ID was last, which needn't be where DV's own
            // copy says (a dedicated host's copy is its parked body).
            JObject self = (root[PlayersKey] as JObject)?.GetJObject(LocalId.ToString());
            Vector3? position = self?.GetVector3(SaveGameKeys.Player_position);
            if (position.HasValue)
            {
                data.SetVector3(SaveGameKeys.Player_position, position.Value);
                data.SetVector3(SaveGameKeys.Player_rotation, new Vector3(0f, self.GetFloat(SaveGameKeys.Player_rotation) ?? 0f, 0f));
                data.SetString(SaveGameKeys.Player_car_guid, string.Empty);
            }
            LocalOwnedItems = OwnedInSave(root, LocalId);
        }

        // The local inventory comes back from the server at the local player's login, like any
        // player's, not from DV's copy. On a dedicated host that leaves the parked body empty.
        data.SetObject(SaveGameKeys.Storage_Inventory, new List<StorageItemData>());
        Multiplayer.Log($"Save format: per-player (local player {(lifecycle.IsDedicatedHost ? "is the dedicated host's parked body" : $"owns {LocalOwnedItems?.Count ?? 0} saved item(s)")})");
    }

    // A career saved before this format: the host player's state is only in DV's fields. Move it to
    // the owner's ID, as if they had been a client all along. Other players' entries are already
    // per player (under install GUIDs, which InstallGuidRekey moves at their login). Runs once:
    // the marker is set after it.
    private static void Convert(SaveGameData data, JObject root, Guid owner)
    {
        int moved = ConvertSave(root, owner, data.GetVector3(SaveGameKeys.Player_position),
            data.GetVector3(SaveGameKeys.Player_rotation)?.y ?? 0f,
            StorageSerializer.LoadStorageData(StorageType.Inventory, data));
        Multiplayer.Log($"Save format: converted a career saved before per-player state; {moved} inventory item(s) and the position now belong to {owner}");
    }

    /// <summary>
    /// Moves the state DV keeps for the local player (its position, rotation and inventory) into
    /// the save's Multiplayer root, owned by <paramref name="owner"/>. Converting the same root
    /// twice changes nothing the second time: an existing entry for the owner is kept, and an item
    /// already recorded isn't recorded again (an item without a persistent ID gets one derived from
    /// the owner, its place in the inventory and its prefab). Job paperwork is left to DV's job save.
    /// Returns how many items it recorded.
    /// </summary>
    public static int ConvertSave(JObject root, Guid owner, Vector3? position, float rotationY,
        IEnumerable<StorageItemData> inventory)
    {
        JObject players = root.GetJObject(PlayersKey) ?? new JObject();
        string key = owner.ToString();
        if (players.GetJObject(key) == null)
        {
            JObject entry = new JObject();
            if (position.HasValue)
                entry.SetVector3(SaveGameKeys.Player_position, position.Value);
            entry.SetFloat(SaveGameKeys.Player_rotation, rotationY);
            players.SetJObject(key, entry);
        }
        root.SetJObject(PlayersKey, players);

        JArray records = root[WorldItemPersistenceManager.SaveKey] as JArray ?? new JArray();
        HashSet<Guid> known = new(records.OfType<JObject>()
            .Select(value => Guid.TryParse((string)value["persistentItemId"], out Guid id) ? id : Guid.Empty));
        int moved = 0;
        int index = -1;
        foreach (StorageItemData item in inventory ?? new List<StorageItemData>())
        {
            index++;
            if (item == null || string.IsNullOrEmpty(item.itemPrefabName) || WorldItemPersistenceManager.IsJobSystemDocument(item.itemPrefabName))
                continue;
            Guid id = Guid.TryParse((string)item.state?[global::Multiplayer.Shared.Items.PersistentWorldItemIdentityPatch.Key], out Guid saved) && saved != Guid.Empty
                ? saved : ConvertedItemId(owner, index, item);
            if (!known.Add(id))
                continue;   // already a record: nothing to move
            records.Add(new WorldItemPersistenceRecord
            {
                PersistentItemId = id,
                PrefabName = item.itemPrefabName,
                Placement = ItemPlacementKind.PlayerInventory,
                OwnerIdentity = item.belongsToPlayer ? owner : Guid.Empty,
                PlacementPlayerIdentity = owner,
                ClaimSlot = item.inventorySlotIndex,
                State = item.state,
            }.ToJson());
            moved++;
        }
        root[WorldItemPersistenceManager.SaveKey] = records;
        return moved;
    }

    // The same for the same item in the same career, so converting twice records it once.
    private static Guid ConvertedItemId(Guid owner, int index, StorageItemData item)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
            $"converted-inventory|{owner:D}|{index}|{item.inventorySlotIndex}|{item.itemPrefabName}"));
        return new Guid(hash);
    }

    /// <summary>
    /// What a player ID owns according to the save: its item records, Lost and Found entries and
    /// cold-container contents, by prefab and inventory slot.
    /// </summary>
    public static List<StorageItemData> OwnedInSave(JObject root, Guid owner)
    {
        List<StorageItemData> owned = new();
        string identity = owner.ToString("D");

        foreach (JObject value in (root[WorldItemPersistenceManager.SaveKey] as JArray ?? new JArray()).OfType<JObject>())
        {
            WorldItemPersistenceRecord record = WorldItemPersistenceRecord.FromJson(value);
            if (record.IsTombstone || string.IsNullOrEmpty(record.PrefabName) ||
                (record.OwnerIdentity != owner && record.PlacementPlayerIdentity != owner))
                continue;
            int slot = record.Placement == ItemPlacementKind.PlayerInventory ? record.ClaimSlot : -1;
            owned.Add(new StorageItemData(record.PrefabName, Vector3.zero, Quaternion.identity, belongsToPlayer: true, inventorySlotIndex: slot));
        }

        foreach (JObject value in (root["LostAndFound"] as JArray ?? new JArray()).OfType<JObject>())
            if (string.Equals((string)value["ownerIdentity"], identity, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty((string)value["prefabName"]))
                owned.Add(new StorageItemData((string)value["prefabName"], Vector3.zero, Quaternion.identity, belongsToPlayer: true));

        try
        {
            string encoded = (string)root["ColdContainers"];
            if (!string.IsNullOrWhiteSpace(encoded) &&
                ColdContainerSnapshotCodec.TryDecode(System.Convert.FromBase64String(encoded), out ColdContainerGraphSnapshot snapshot, out _))
                foreach (var item in snapshot.Items)
                    if (string.Equals(item.PersistentOwnerIdentity, identity, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(item.PrefabName))
                        owned.Add(new StorageItemData(item.PrefabName, Vector3.zero, Quaternion.identity, belongsToPlayer: true));
        }
        catch (Exception e)
        {
            Multiplayer.LogWarning($"Save format: couldn't read cold containers for the starting-item check: {e.Message}");
        }
        return owned;
    }
}
