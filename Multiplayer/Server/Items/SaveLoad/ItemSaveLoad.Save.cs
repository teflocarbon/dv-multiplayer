using DV.CabControls;
using DV.InventorySystem;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items.SaveLoad;

internal static partial class ItemSaveLoad
{
    public static void WriteSave(JObject multiplayerRoot)
    {
        if (multiplayerRoot == null || NetworkLifecycle.Instance?.IsHost() != true)
            return;

        TryImport();
        if (!importAttempted)
            return;

        JArray records = new();
        foreach (LiveItems.LiveItem record in RecordsRequiringPersistence())
        {
            if (!NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item?.Item == null)
                continue;

            records.Add(CreateSaveRecord(record, item).ToJson());
        }

        foreach (JObject tombstone in destroyedAuthoredRecords)
            records.Add(tombstone.DeepClone());

        multiplayerRoot[SaveKey] = records;
    }

    private static System.Collections.Generic.IEnumerable<LiveItems.LiveItem>
        RecordsRequiringPersistence() =>
        LiveItems.GetAll().Where(record =>
            ShouldPersist(record) || NetworkedItemManager.Instance?.HasActiveSpatialState(record.NetId) == true);

    private static SavedItemRow CreateSaveRecord(LiveItems.LiveItem record,
        NetworkedItem item)
    {
        ItemSpatialStateData checkpoint = null;
        NetworkedItemManager.Instance?.TryGetLatestSpatialState(record.NetId, out checkpoint);
        SpatialCheckpoint spatial = SpatialCheckpoint.From(record, checkpoint);

        return new SavedItemRow
        {
            PersistentItemId = record.PersistentItemId,
            AuthoredItemKey = record.AuthoredItemKey,
            PrefabName = record.PrefabName,
            Revision = record.Revision,
            HasPersistentOverride = record.HasPersistentOverride || checkpoint != null,
            IsReplenishableStockFork = record.IsReplenishableStockFork,
            // An item in a player's hand when the host saves goes back into their inventory.
            Placement = spatial.Placement == ItemPlacementKind.PlayerHand && IsPlayerPossession(record)
                ? ItemPlacementKind.PlayerInventory : spatial.Placement,
            OwnerIdentity = ResolveSavedOwnerIdentity(record),
            PlacementPlayerIdentity = ResolvePlacementIdentity(record),
            ClaimSlot = record.InventoryClaimSlot,
            ClaimFlags = record.InventoryClaimFlags,
            WorldParentKind = spatial.ParentKind,
            WorldParentNetId = spatial.ParentNetId,
            WorldParentKey = spatial.ParentKey,
            WorldParentPersistentId = record.WorldParentPersistentId,
            AttachedCarPersistentId = record.AttachedCarPersistentId,
            AttachedFront = record.AttachedFront,
            ParentLocalPosition = spatial.ParentLocalPosition,
            ParentLocalRotation = spatial.ParentLocalRotation,
            Position = spatial.Position,
            Rotation = spatial.Rotation,
            State = TrySaveItemState(record.NetId, item)
        };
    }

    private static Guid ResolveSavedOwnerIdentity(LiveItems.LiveItem record)
    {
        if (record.PersistentOwnerIdentity != Guid.Empty)
            return record.PersistentOwnerIdentity;

        return NetworkServer.Instance.ServerPlayers
                   .FirstOrDefault(player => player.PlayerId == record.PersistentOwnerPlayerId)?.Guid ?? Guid.Empty;
    }

    private static JObject TrySaveItemState(ushort netId, NetworkedItem item)
    {
        try
        {
            return item.GetComponent<ItemSaveData>()?.SaveItemData();
        }
        catch (Exception ex)
        {
            Multiplayer.LogWarning($"World item state save failed for {netId}: {ex.Message}\r\n{ex.StackTrace}");
            return null;
        }
    }

    /// <summary>
    /// Job overviews and booklets belong to DV's job system, which saves jobs and recreates their
    /// paperwork on load. Persisting them as world items as well restored a dead copy of every
    /// board overview on each load, piling stacked duplicates onto the job boards.
    /// </summary>
    internal static bool IsJobSystemDocument(string prefabName) =>
        prefabName is "JobOverview" or "JobBooklet";

    /// <summary>
    /// An item in a player's inventory or hand, connected or not, the host's own player included:
    /// every player's possessions are saved by their GUID (PerPlayerSaveFormat). Restored, it waits
    /// for that player to connect and goes back into their inventory, wherever the server runs.
    /// </summary>
    internal static bool IsPlayerPossession(LiveItems.LiveItem record) =>
        record?.Placement is ItemPlacementKind.PlayerInventory or ItemPlacementKind.PlayerHand &&
        ResolvePlacementIdentity(record) != Guid.Empty;

    // The GUID of the player holding an item. A record of the host's own player's item may carry
    // only the runtime player ID.
    private static Guid ResolvePlacementIdentity(LiveItems.LiveItem record)
    {
        if (record.PlacementPlayerIdentity != Guid.Empty)
            return record.PlacementPlayerIdentity;
        if (record.PlacementPlayerId == 0)
            return Guid.Empty;
        NetworkServer server = NetworkServer.Instance;
        ServerPlayer holder = server?.ServerPlayers.FirstOrDefault(player => player.PlayerId == record.PlacementPlayerId);
        return holder?.Guid ?? Guid.Empty;
    }

    private static bool ShouldPersist(LiveItems.LiveItem record)
    {
        if (record == null || record.Placement is ItemPlacementKind.Container or
            ItemPlacementKind.LostAndFound or ItemPlacementKind.Installed or ItemPlacementKind.Destroyed)
            return false;
        if (IsJobSystemDocument(record.PrefabName))
            return false;

        if (record.PersistentItemId != Guid.Empty)
        {
            // Whatever a player carries is theirs to keep, as vanilla's inventory keeps everything
            // in it: an item found in the world (a mug) has no BelongsToPlayer, and was lost from
            // the inventory at the next load.
            if (IsPlayerPossession(record))
                return NetworkedItem.TryGet(record.NetId, out NetworkedItem carried) && carried?.Item != null;

            // Like vanilla's world storage, only keep dynamic items a player owns. Unowned ones
            // are respawned by their source each load (e.g. the paint station's spray can, which
            // vanilla only skips when the player already owns one), so persisting them added a
            // copy every load. An item with an owner is a player's whatever its prefab says: a mug
            // restored from the save is a fresh prefab copy without BelongsToPlayer, and was lost at
            // the save after (found by ledger-save, 2026-10-21).
            if (!NetworkedItem.TryGet(record.NetId, out NetworkedItem item) ||
                item?.Item?.InventorySpecs?.BelongsToPlayer != true && record.PersistentOwnerIdentity == Guid.Empty)
                return false;
            return record.Placement is ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
                ItemPlacementKind.StaticParent || IsPlayerPossession(record);
        }

        if (!WorldItemStableIdentity.IsValid(record.AuthoredItemKey))
            return false;

        return record.HasPersistentOverride ||
               (record.Position - record.BaselinePosition).sqrMagnitude > 0.0001f ||
               Quaternion.Angle(record.Rotation, record.BaselineRotation) > 0.1f ||
               record.Placement != record.BaselinePlacement;
    }

    private struct SpatialCheckpoint
    {
        public ItemPlacementKind Placement { get; private set; }
        public Vector3 Position { get; private set; }
        public Quaternion Rotation { get; private set; }
        public ItemWorldParentKind ParentKind { get; private set; }
        public ushort ParentNetId { get; private set; }
        public string ParentKey { get; private set; }
        public Vector3 ParentLocalPosition { get; private set; }
        public Quaternion ParentLocalRotation { get; private set; }

        public static SpatialCheckpoint From(LiveItems.LiveItem record,
            ItemSpatialStateData checkpoint)
        {
            ItemWorldParentKind parentKind = checkpoint?.WorldParentKind ?? record.WorldParentKind;
            return new SpatialCheckpoint
            {
                Placement = checkpoint == null ? record.Placement : PlacementFrom(parentKind),
                Position = checkpoint?.AbsolutePosition ?? record.Position,
                Rotation = checkpoint?.Rotation ?? record.Rotation,
                ParentKind = parentKind,
                ParentNetId = checkpoint?.WorldParentNetId ?? record.WorldParentNetId,
                ParentKey = checkpoint?.WorldParentKey ?? record.WorldParentKey ?? string.Empty,
                ParentLocalPosition = checkpoint?.ParentLocalPosition ?? record.ParentLocalPosition,
                ParentLocalRotation = checkpoint?.ParentLocalRotation ?? record.ParentLocalRotation
            };
        }

        private static ItemPlacementKind PlacementFrom(ItemWorldParentKind parentKind) => parentKind switch
        {
            ItemWorldParentKind.TrainInterior => ItemPlacementKind.TrainInterior,
            ItemWorldParentKind.StaticParent => ItemPlacementKind.StaticParent,
            _ => ItemPlacementKind.World
        };
    }
}
