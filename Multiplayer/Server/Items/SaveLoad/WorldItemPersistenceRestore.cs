using DV;
using DV.CabControls;
using DV.InventorySystem;
using DV.JObjectExtstensions;
using DV.ThingTypes;
using Multiplayer.Server.Gadgets;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Debugging.Protocol;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items.SaveLoad;

internal static partial class WorldItemPersistenceManager
{
    private static void TryImport()
    {
        if (!CanImport())
            return;

        importAttempted = true;
        JArray saved = SaveGameManager.Instance.data.GetJObject("Multiplayer")?[SaveKey] as JArray;
        if (saved == null)
            return;

        int restored = 0;
        foreach (JObject value in saved.OfType<JObject>())
        {
            try
            {
                if (Restore(WorldItemPersistenceRecord.FromJson(value), value))
                    restored++;
            }
            catch (Exception ex)
            {
                Multiplayer.LogError($"World item restore failed: {ex.Message}\r\n{ex.StackTrace}");
            }
        }

        DebugRuntime.Publish("item-world", "world-item.persistence-imported", DebugRuntimeSide.Server,
            data: new Dictionary<string, object>
            {
                ["savedCount"] = saved.Count,
                ["restoredCount"] = restored
            });
    }

    // The map's terrain tops out a few hundred metres up; DV parks inventory items ~5 km up.
    private const float MaxPlausibleWorldHeight = 1500f;

    private static bool CanImport() => !importAttempted && StartingItemsController.Instance != null &&
        StartingItemsController.Instance.itemsLoaded && SaveGameManager.Instance?.data != null;

    private static bool Restore(WorldItemPersistenceRecord saved, JObject source)
    {
        if (saved.IsTombstone)
            return RestoreAuthoredTombstone(saved, source);

        // Earlier builds recorded the host's inventory items as World (inventory adds weren't
        // observed) and persisted them at DV's hidden inventory position, kilometres up. Restoring
        // those dropped them out of the sky every load, duplicating the inventory and punching
        // items through the terrain. No real world item can be that high: skip them.
        // Saves from earlier builds contain job paperwork the job system recreates by itself.
        if (IsJobSystemDocument(saved.PrefabName))
            return false;

        if (saved.Placement == ItemPlacementKind.World && saved.Position.y > MaxPlausibleWorldHeight)
        {
            Multiplayer.LogWarning($"Discarding persisted world item {saved.PrefabName} at height {saved.Position.y:0} " +
                                   "(an inventory item saved at the hidden inventory position by an older build)");
            return false;
        }

        NetworkedItem item = FindOrCreateRepresentation(saved);
        if (item == null)
            return false;
        // A fresh copy of the prefab: an owned item is a player's, as it was when it was saved, so
        // vanilla (its world storage, Lost and Found) treats it as one.
        if (saved.OwnerIdentity != Guid.Empty && item.Item?.InventorySpecs != null)
            item.Item.InventorySpecs.BelongsToPlayer = true;
        // Older builds saved installed gadgets here as world items as well as in vanilla's installed
        // storage. Binding the row to the installed item would make it a loose item and place it again.
        if (GadgetItemTransitions.IsInstalled(item))
        {
            Multiplayer.LogWarning($"Ignoring saved world item {saved.PrefabName} ({saved.PersistentItemId:D}): " +
                                   "it is an installed gadget, saved as a world item by an older build");
            return false;
        }

        item.transform.SetPositionAndRotation(saved.Position + WorldMover.currentMove, saved.Rotation);

        ServerPlayer owner = ResolveConnectedPlayer(saved.OwnerIdentity);
        ServerPlayer holder = ResolveConnectedPlayer(saved.PlacementPlayerIdentity);
        ItemUpdateData seed = CreateRestoreSeed(item, saved, holder);
        AuthoritativeItemRegistry.Record record = AuthoritativeItemRegistry.Ensure(item, owner, seed);
        ApplySavedRecord(record, saved, owner, holder);
        AuthoritativeItemRegistry.WriteToSnapshot(record, seed, ItemTransitionReason.PersistenceRestore);

        bool waitingForOwner = MustWaitForHolder(saved, holder);
        if (waitingForOwner)
            DeferUntilHolderConnects(item);
        else
            item.ApplyServerCanonicalSnapshot(seed);

        if (saved.State != null)
            DeferItemStateRestore(item, saved);
        else if (!waitingForOwner)
            ServerItemManager.Instance?.RegisterHostWorldItem(item);

        return true;
    }

    private static NetworkedItem FindOrCreateRepresentation(WorldItemPersistenceRecord saved)
    {
        NetworkedItem existing = FindExisting(saved.PersistentItemId, saved.AuthoredItemKey);
        if (existing != null || saved.PersistentItemId == Guid.Empty)
            return existing;

        InventoryItemSpec spec = Globals.G.Items.items.FirstOrDefault(candidate =>
            string.Equals(candidate.ItemPrefabName, saved.PrefabName, StringComparison.Ordinal));
        if (spec == null)
            return null;

        GameObject instance = UnityEngine.Object.Instantiate(spec.gameObject,
            saved.Position + WorldMover.currentMove, saved.Rotation);
        PersistentWorldItemIdentity identity = instance.GetComponent<PersistentWorldItemIdentity>() ??
                                               instance.AddComponent<PersistentWorldItemIdentity>();
        identity.Value = saved.PersistentItemId;
        return instance.GetComponent<NetworkedItem>() ?? instance.AddComponent<NetworkedItem>();
    }

    private static ItemUpdateData CreateRestoreSeed(NetworkedItem item, WorldItemPersistenceRecord saved,
        ServerPlayer holder)
    {
        ItemUpdateData seed = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.ItemState) ??
                              new ItemUpdateData();
        seed.ItemState = ItemStateFrom(saved.Placement);
        seed.PlayerId = holder?.PlayerId ?? 0;
        seed.ItemPosition = saved.Position;
        seed.ItemRotation = saved.Rotation;
        return seed;
    }

    private static ItemState ItemStateFrom(ItemPlacementKind placement) => placement switch
    {
        ItemPlacementKind.PlayerHand => ItemState.InHand,
        ItemPlacementKind.PlayerInventory => ItemState.InInventory,
        ItemPlacementKind.Attached or ItemPlacementKind.SnappedAttachment => ItemState.Attached,
        _ => ItemState.Dropped
    };

    private static void ApplySavedRecord(AuthoritativeItemRegistry.Record record,
        WorldItemPersistenceRecord saved, ServerPlayer owner, ServerPlayer holder)
    {
        record.Revision = saved.Revision;
        record.Placement = saved.Placement;
        record.PlacementPlayerId = holder?.PlayerId ?? 0;
        record.PlacementHand = PlayerHand.None;
        record.PlacementPlayerIdentity = saved.PlacementPlayerIdentity;
        record.PersistentOwnerPlayerId = owner?.PlayerId ?? OwnedItemsServer.OwnerNumber(saved.OwnerIdentity);
        record.PersistentOwnerIdentity = saved.OwnerIdentity;
        record.InventoryClaimPlayerId = owner?.PlayerId ?? 0;
        record.InventoryClaimSlot = saved.ClaimSlot;
        record.InventoryClaimFlags = saved.ClaimFlags;
        record.Position = saved.Position;
        record.Rotation = saved.Rotation;
        record.LastReason = ItemTransitionReason.PersistenceRestore;
        // Kept, not logged: a load restores every saved item at once. A held item is logged as it
        // reaches its holder (WorldItemPersistenceManager.BindConnectedOwner).
        record.LastMove = ItemChangeLog.Move(global::Multiplayer.Core.Items.ChangeCauses.ItemMoveReason.RestoredFromSave,
            detail: "saved as " + saved.Placement);
        record.PersistentItemId = saved.PersistentItemId;
        record.AuthoredItemKey = saved.AuthoredItemKey;
        record.WorldParentKind = saved.WorldParentKind;
        record.WorldParentNetId = saved.WorldParentNetId;
        record.WorldParentKey = saved.WorldParentKey;
        record.WorldParentPersistentId = saved.WorldParentPersistentId;
        ResolveWorldParent(record);
        record.AttachedCarPersistentId = saved.AttachedCarPersistentId;
        record.AttachedFront = saved.AttachedFront;
        ResolveAttachedCar(record);
        record.ParentLocalPosition = saved.ParentLocalPosition;
        record.ParentLocalRotation = saved.ParentLocalRotation;
        record.HasPersistentOverride = saved.HasPersistentOverride;
        record.IsReplenishableStockFork = saved.IsReplenishableStockFork;
    }

    private static void ResolveWorldParent(AuthoritativeItemRegistry.Record record)
    {
        if (record.WorldParentKind == ItemWorldParentKind.TrainInterior && record.WorldParentNetId == 0 &&
            TryResolveTrain(record.WorldParentPersistentId, out TrainCar parentCar))
            NetworkedTrainCar.TryGetNetId(parentCar, out record.WorldParentNetId);
    }

    private static void ResolveAttachedCar(AuthoritativeItemRegistry.Record record)
    {
        if (record.Placement == ItemPlacementKind.Attached &&
            TryResolveTrain(record.AttachedCarPersistentId, out TrainCar attachedCar))
            NetworkedTrainCar.TryGetNetId(attachedCar, out record.AttachedCarNetId);
    }

    private static bool MustWaitForHolder(WorldItemPersistenceRecord saved, ServerPlayer holder) =>
        holder == null && saved.PlacementPlayerIdentity != Guid.Empty &&
        saved.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory;

    private static void DeferUntilHolderConnects(NetworkedItem item)
    {
        pendingOwnerRepresentations.Add(item);
        ServerItemManager.Instance?.UnregisterHostWorldItem(item);
        item.gameObject.SetActive(false);
    }

    private static void DeferItemStateRestore(NetworkedItem item, WorldItemPersistenceRecord saved)
    {
        pendingRepresentations.Add(item);
        ServerItemManager.Instance?.UnregisterHostWorldItem(item);
        pendingStateRestores.Add(new PendingStateRestore
        {
            Item = item,
            State = (JObject)saved.State.DeepClone(),
            Prefab = saved.PrefabName,
            NextAttemptAt = Time.realtimeSinceStartup + 0.25f
        });
    }

    private static void ProcessPendingStateRestores()
    {
        float now = Time.realtimeSinceStartup;
        foreach (PendingStateRestore pending in pendingStateRestores.ToArray())
        {
            if (pending.Item == null)
            {
                pendingStateRestores.Remove(pending);
                continue;
            }

            if (now < pending.NextAttemptAt)
                continue;

            TryRestorePendingState(pending, now);
        }
    }

    private static void TryRestorePendingState(PendingStateRestore pending, float now)
    {
        try
        {
            ItemSaveData saveData = pending.Item.GetComponent<ItemSaveData>();
            if (saveData == null)
                throw new InvalidOperationException("missing-ItemSaveData");

            saveData.LoadItemData((JObject)pending.State.DeepClone());
            CompletePendingStateRestore(pending);
        }
        catch (Exception ex)
        {
            pending.Attempts++;
            if (pending.Attempts < 5)
            {
                pending.NextAttemptAt = now + 0.25f * pending.Attempts;
                return;
            }

            FailPendingStateRestore(pending, ex);
        }
    }

    private static void CompletePendingStateRestore(PendingStateRestore pending)
    {
        RemovePendingStateRestore(pending);
        ServerItemManager.Instance?.RegisterHostWorldItem(pending.Item);
        DebugRuntime.Publish("item-world", "world-item.persistence-state-restored", DebugRuntimeSide.Server,
            entityType: "Item", entityId: pending.Item.NetId.ToString(), data: new Dictionary<string, object>
            {
                ["prefabName"] = pending.Prefab ?? string.Empty,
                ["attempts"] = pending.Attempts + 1
            });
    }

    private static void FailPendingStateRestore(PendingStateRestore pending, Exception error)
    {
        RemovePendingStateRestore(pending);
        ServerItemManager.Instance?.RegisterHostWorldItem(pending.Item);
        Multiplayer.LogError($"World item state restore permanently failed for {pending.Prefab}: {error.Message}");
        DebugRuntime.Publish("item-world", "world-item.persistence-state-restore-failed",
            DebugRuntimeSide.Server, DebugSeverity.Error, "Item", pending.Item.NetId.ToString(),
            new Dictionary<string, object>
            {
                ["prefabName"] = pending.Prefab ?? string.Empty,
                ["attempts"] = pending.Attempts,
                ["error"] = error.Message
            });
    }

    private static void RemovePendingStateRestore(PendingStateRestore pending)
    {
        pendingStateRestores.Remove(pending);
        pendingRepresentations.Remove(pending.Item);
    }

    private static bool RestoreAuthoredTombstone(WorldItemPersistenceRecord saved, JObject source)
    {
        if (!WorldItemStableIdentity.IsValid(saved.AuthoredItemKey) ||
            !NetworkedItemManager.Instance.TryGetAuthoredItem(saved.AuthoredItemKey, out NetworkedItem item) ||
            item == null)
            return false;

        ServerItemManager.Instance?.UnregisterHostWorldItem(item);
        item.gameObject.SetActive(false);
        ReplaceAuthoredTombstone((JObject)source.DeepClone());

        // Canonicalize the tombstone before the first dirty scan can rediscover the authored object.
        AuthoritativeItemRegistry.Record record = AuthoritativeItemRegistry.Ensure(item);
        if (record == null)
            return false;

        record.AuthoredItemKey = saved.AuthoredItemKey;
        record.PersistentItemId = Guid.Empty;
        record.Placement = ItemPlacementKind.Destroyed;
        record.PlacementHand = PlayerHand.None;
        record.Revision = saved.Revision;
        record.LastReason = ItemTransitionReason.PersistenceRestore;
        return true;
    }

    private static NetworkedItem FindExisting(Guid persistentId, string authoredKey)
    {
        if (WorldItemStableIdentity.IsValid(authoredKey) &&
            NetworkedItemManager.Instance.TryGetAuthoredItem(authoredKey, out NetworkedItem authored))
            return authored;
        if (persistentId == Guid.Empty)
            return null;

        return NetworkedItem.GetAll().Where(item => item != null).Distinct().FirstOrDefault(item =>
            item.GetComponent<PersistentWorldItemIdentity>()?.Value == persistentId);
    }

    private static bool TryResolveTrain(string carGuid, out TrainCar trainCar)
    {
        trainCar = null;
        if (string.IsNullOrEmpty(carGuid))
            return false;

        trainCar = Resources.FindObjectsOfTypeAll<TrainCar>().FirstOrDefault(candidate =>
            candidate != null && string.Equals(candidate.CarGUID, carGuid, StringComparison.Ordinal));
        return trainCar != null;
    }

    private static ServerPlayer ResolveConnectedPlayer(Guid identity)
    {
        if (identity == Guid.Empty)
            return null;
        return NetworkServer.Instance.ServerPlayers.FirstOrDefault(player => player.Guid == identity);
    }
}
