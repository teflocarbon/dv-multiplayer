using DV;
using DV.InventorySystem;
using DV.ThingTypes;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Debugging.Protocol;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Server.Items.SaveLoad;

/// <summary>
/// Coordinates host persistence for free world items and scene-authored overrides.
/// Save encoding and restoration live in the other partial declarations beside this file.
/// </summary>
internal static partial class ItemSaveLoad
{
    internal const string SaveKey = "WorldItemsV1";

    private static bool importAttempted;

    /// <summary>The save's records have been read (the owned-items ledger is checked against them after).</summary>
    internal static bool ImportAttempted => importAttempted;
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

            if (!LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record) ||
                record.PlacementPlayerIdentity != player.Guid)
                continue;

            BindConnectedOwner(item, record, player);
        }
    }

    private static void BindConnectedOwner(NetworkedItem item, LiveItems.LiveItem record,
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

        // The saved holder is back: what was held for them goes into their inventory.
        ChangeCause<ItemMoveReason> restored =
            ItemChangeLog.Move(ItemMoveReason.RestoredFromSave, player,
                "kept for them since the save, now in their " + record.Placement);
        ItemChangeLog.RecordMove(item, record, restored);
        if (LiveItems.TryCreateCorrectionSnapshot(item, out ItemUpdateData snapshot, restored))
        {
            item.gameObject.SetActive(true);
            item.ApplyServerCanonicalSnapshot(snapshot);
        }

        ServerItemManager.Instance?.RegisterHostWorldItem(item);
    }

    public static bool OwnsWorldPersistence(NetworkedItem item)
    {
        if (item == null || item.NetId == 0 ||
            !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record))
            return false;

        if (item.IsSceneAuthored)
            return record.Placement is ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
                ItemPlacementKind.StaticParent or ItemPlacementKind.PlayerHand or
                ItemPlacementKind.PlayerInventory or ItemPlacementKind.Attached or
                ItemPlacementKind.SnappedAttachment or ItemPlacementKind.Destroyed;

        return record.Placement is ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
            ItemPlacementKind.StaticParent or ItemPlacementKind.Destroyed || IsPlayerPossession(record);
    }

    /// <summary>
    /// Remove multiplayer-owned world entries emitted by DV before our save postfix.
    /// keepInventory leaves DV's inventory list alone: in the main save it is the local player's
    /// copy for vanilla DV (PerPlayerSaveFormat), which the mod doesn't read back.
    /// </summary>
    public static void RemoveVanillaStorageDuplicates(SaveGameData saveGameData, bool keepInventory = false)
    {
        if (saveGameData == null)
            return;

        int removed = RemoveManagedStorageItems(saveGameData, StorageType.World, SaveGameKeys.Storage_World) +
                      (keepInventory ? 0 : RemoveManagedStorageItems(saveGameData, StorageType.Inventory,
                          SaveGameKeys.Storage_Inventory));
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
            item?.state?[ItemIdentityPatch.ManagedWorldKey]?.Value<bool>() == true);
        if (removed > 0)
            saveGameData.SetObject(key, items);
        return removed;
    }

    public static void RecordDestroyed(NetworkedItem item)
    {
        if (item == null || !item.IsSceneAuthored ||
            !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record))
            return;

        ReplaceAuthoredTombstone(CreateTombstone(record).ToJson());
    }

    private static SavedItemRow CreateTombstone(LiveItems.LiveItem record) =>
        new()
        {
            AuthoredItemKey = record.AuthoredItemKey ?? string.Empty,
            PrefabName = record.PrefabName ?? string.Empty,
            Revision = record.Revision == uint.MaxValue ? record.Revision : record.Revision + 1,
            Placement = ItemPlacementKind.Destroyed
        };

    private static void ReplaceAuthoredTombstone(JObject tombstone)
    {
        string authoredKey = (string)tombstone?["authoredItemKey"] ?? string.Empty;
        destroyedAuthoredRecords.RemoveAll(existing =>
            string.Equals((string)existing["authoredItemKey"], authoredKey, StringComparison.Ordinal));
        if (tombstone != null)
            destroyedAuthoredRecords.Add(tombstone);
    }
}
