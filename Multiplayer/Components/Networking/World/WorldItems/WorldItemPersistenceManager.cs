using DV;
using DV.InventorySystem;
using DV.ThingTypes;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Patches.World.Items;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Components.Networking.World.WorldItems;

/// <summary>
/// Coordinates host persistence for free world items and scene-authored overrides.
/// Save encoding and restoration live in the other partial declarations beside this file.
/// </summary>
internal static partial class WorldItemPersistenceManager
{
    private const string SaveKey = "WorldItemsV1";

    private static bool importAttempted;
    private static readonly List<JObject> destroyedAuthoredRecords = new();
    private static readonly List<PendingStateRestore> pendingStateRestores = new();
    private static readonly HashSet<NetworkedItem> pendingRepresentations = new();
    private static readonly HashSet<NetworkedItem> pendingOwnerRepresentations = new();

    private sealed class PendingStateRestore
    {
        public NetworkedItem Item;
        public JObject State;
        public string Prefab;
        public int Attempts;
        public float NextAttemptAt;
    }

    public static void Clear()
    {
        importAttempted = false;
        destroyedAuthoredRecords.Clear();
        pendingStateRestores.Clear();
        pendingRepresentations.Clear();
        pendingOwnerRepresentations.Clear();
        WorldItemStaticParentRegistry.Clear();
    }

    public static void HostTick()
    {
        if (NetworkLifecycle.Instance?.IsHost() != true)
            return;

        TryImport();
        ProcessPendingStateRestores();
    }

    public static bool IsRestorePending(NetworkedItem item) =>
        item != null && (pendingRepresentations.Contains(item) || pendingOwnerRepresentations.Contains(item));

    public static void OnOwnerConnected(ServerPlayer player)
    {
        if (player == null)
            return;

        foreach (NetworkedItem item in pendingOwnerRepresentations.ToArray())
        {
            if (item == null)
            {
                pendingOwnerRepresentations.Remove(item);
                continue;
            }

            if (!AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record) ||
                record.PlacementPlayerIdentity != player.Guid)
                continue;

            BindConnectedOwner(item, record, player);
        }
    }

    private static void BindConnectedOwner(NetworkedItem item, AuthoritativeItemRegistry.Record record,
        ServerPlayer player)
    {
        pendingOwnerRepresentations.Remove(item);
        record.PlacementPlayerId = player.PlayerId;

        if (record.PersistentOwnerIdentity == player.Guid)
        {
            record.PersistentOwnerPlayerId = player.PlayerId;
            if (record.InventoryClaimSlot >= 0)
                record.InventoryClaimPlayerId = player.PlayerId;
        }

        if (AuthoritativeItemRegistry.TryCreateCorrectionSnapshot(item, out ItemUpdateData snapshot))
        {
            item.gameObject.SetActive(true);
            item.ApplyServerCanonicalSnapshot(snapshot);
        }

        NetworkedItemManager.Instance.RegisterHostWorldItem(item);
    }

    public static bool OwnsWorldPersistence(NetworkedItem item)
    {
        if (item == null || item.NetId == 0 ||
            !AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record))
            return false;

        if (item.IsSceneAuthored)
            return record.Placement is ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
                ItemPlacementKind.StaticParent or ItemPlacementKind.PlayerHand or
                ItemPlacementKind.PlayerInventory or ItemPlacementKind.Attached or
                ItemPlacementKind.SnappedAttachment or ItemPlacementKind.Destroyed;

        return record.Placement is ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
            ItemPlacementKind.StaticParent or ItemPlacementKind.Destroyed;
    }

    /// <summary>Remove multiplayer-owned world entries emitted by DV before our save postfix.</summary>
    public static void RemoveVanillaStorageDuplicates(SaveGameData saveGameData)
    {
        if (saveGameData == null)
            return;

        int removed = RemoveManagedStorageItems(saveGameData, StorageType.World, SaveGameKeys.Storage_World) +
                      RemoveManagedStorageItems(saveGameData, StorageType.Inventory,
                          SaveGameKeys.Storage_Inventory);
        if (removed == 0)
            return;

        DebugRuntime.Publish("item-world", "world-item.vanilla-save-duplicates-removed",
            DebugRuntimeSide.Server,
            data: new Dictionary<string, object> { ["removedCount"] = removed });
    }

    private static int RemoveManagedStorageItems(SaveGameData saveGameData, StorageType type, string key)
    {
        List<StorageItemData> items = StorageSerializer.LoadStorageData(type, saveGameData) ?? new();
        int removed = items.RemoveAll(item =>
            item?.state?[PersistentWorldItemIdentityPatch.ManagedWorldKey]?.Value<bool>() == true);
        if (removed > 0)
            saveGameData.SetObject(key, items);
        return removed;
    }

    public static void RecordDestroyed(NetworkedItem item)
    {
        if (item == null || !item.IsSceneAuthored ||
            !AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record))
            return;

        ReplaceAuthoredTombstone(WorldItemPersistenceRecord.CreateTombstone(record).ToJson());
    }

    private static void ReplaceAuthoredTombstone(JObject tombstone)
    {
        string authoredKey = (string)tombstone?["authoredItemKey"] ?? string.Empty;
        destroyedAuthoredRecords.RemoveAll(existing =>
            string.Equals((string)existing["authoredItemKey"], authoredKey, StringComparison.Ordinal));
        if (tombstone != null)
            destroyedAuthoredRecords.Add(tombstone);
    }
}
