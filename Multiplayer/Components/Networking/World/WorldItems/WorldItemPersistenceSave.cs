using DV.CabControls;
using DV.InventorySystem;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Managers.Server;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Components.Networking.World.WorldItems;

internal static partial class WorldItemPersistenceManager
{
    public static void WriteSave(JObject multiplayerRoot)
    {
        if (multiplayerRoot == null || NetworkLifecycle.Instance?.IsHost() != true)
            return;

        TryImport();
        if (!importAttempted)
            return;

        JArray records = new();
        foreach (AuthoritativeItemRegistry.Record record in RecordsRequiringPersistence())
        {
            if (!NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item?.Item == null)
                continue;

            records.Add(CreateSaveRecord(record, item).ToJson());
        }

        foreach (JObject tombstone in destroyedAuthoredRecords)
            records.Add(tombstone.DeepClone());

        multiplayerRoot[SaveKey] = records;
    }

    private static System.Collections.Generic.IEnumerable<AuthoritativeItemRegistry.Record>
        RecordsRequiringPersistence() =>
        AuthoritativeItemRegistry.GetAll().Where(record =>
            ShouldPersist(record) || NetworkedItemManager.Instance?.HasActiveSpatialState(record.NetId) == true);

    private static WorldItemPersistenceRecord CreateSaveRecord(AuthoritativeItemRegistry.Record record,
        NetworkedItem item)
    {
        ItemSpatialStateData checkpoint = null;
        NetworkedItemManager.Instance?.TryGetLatestSpatialState(record.NetId, out checkpoint);
        SpatialCheckpoint spatial = SpatialCheckpoint.From(record, checkpoint);

        return new WorldItemPersistenceRecord
        {
            PersistentItemId = record.PersistentItemId,
            AuthoredItemKey = record.AuthoredItemKey,
            PrefabName = record.PrefabName,
            Revision = record.Revision,
            HasPersistentOverride = record.HasPersistentOverride || checkpoint != null,
            IsReplenishableStockFork = record.IsReplenishableStockFork,
            // An item in another player's hand when the host saves goes back into their inventory.
            Placement = spatial.Placement == ItemPlacementKind.PlayerHand && IsRemotePlayerPossession(record)
                ? ItemPlacementKind.PlayerInventory : spatial.Placement,
            OwnerIdentity = ResolveSavedOwnerIdentity(record),
            PlacementPlayerIdentity = record.PlacementPlayerIdentity,
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

    private static Guid ResolveSavedOwnerIdentity(AuthoritativeItemRegistry.Record record)
    {
        if (record.PersistentOwnerIdentity != Guid.Empty)
            return record.PersistentOwnerIdentity;

        return NetworkLifecycle.Instance.Server.ServerPlayers
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
            Multiplayer.LogWarning($"World item state save failed for {netId}: {ex.Message}");
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
    /// An item in another player's inventory or hand, connected or not. Vanilla saves only the
    /// host's own inventory, so without this a friend's purchases and pickups were gone after every
    /// reload. Restored, it waits for that player to connect and goes back into their inventory.
    /// </summary>
    internal static bool IsRemotePlayerPossession(AuthoritativeItemRegistry.Record record)
    {
        if (record?.Placement is not (ItemPlacementKind.PlayerInventory or ItemPlacementKind.PlayerHand) ||
            record.PlacementPlayerIdentity == Guid.Empty)
            return false;
        NetworkServer server = NetworkLifecycle.Instance?.Server;
        return server == null || !server.TryGetServerPlayer(server.SelfId, out ServerPlayer host) ||
               host.Guid != record.PlacementPlayerIdentity;
    }

    private static bool ShouldPersist(AuthoritativeItemRegistry.Record record)
    {
        if (record == null || record.Placement is ItemPlacementKind.Container or
            ItemPlacementKind.LostAndFound or ItemPlacementKind.Installed or ItemPlacementKind.Destroyed)
            return false;
        if (IsJobSystemDocument(record.PrefabName))
            return false;

        if (record.PersistentItemId != Guid.Empty)
        {
            // Like vanilla's world storage, only keep dynamic items a player owns. Unowned ones
            // are respawned by their source each load (e.g. the paint station's spray can, which
            // vanilla only skips when the player already owns one), so persisting them added a
            // copy every load.
            if (!NetworkedItem.TryGet(record.NetId, out NetworkedItem item) ||
                item?.Item?.InventorySpecs?.BelongsToPlayer != true)
                return false;
            return record.Placement is ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
                ItemPlacementKind.StaticParent || IsRemotePlayerPossession(record);
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

        public static SpatialCheckpoint From(AuthoritativeItemRegistry.Record record,
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
