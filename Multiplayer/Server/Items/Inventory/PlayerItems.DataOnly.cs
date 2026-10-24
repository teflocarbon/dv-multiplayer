using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV;
using DV.CabControls;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Server.Items.Inventory;

internal static partial class PlayerItems
{
    // No duplicate on the server (docs/design/items/inventory.md, "Data only on the server";
    // OI-COLD-06): an item in a connected remote player's inventory is data on the server, as a cold
    // item is, while that player's game holds the only object. Its record stays (its NetId, holder,
    // slot and revision: it is live), its row is in ColdInventories, and the server's object goes
    // once the holder has been sent the item. Anything that needs the object again (every request
    // naming the item) has it made from the row first, under the same NetId.

    // How long an item sits in a remote inventory, unchanged, before the server's object goes.
    private const float SettleSeconds = 1f;
    private const float ScanSeconds = 0.25f;

    private static readonly Dictionary<ushort, float> settlingSince = new();
    private static readonly HashSet<ushort> materializing = new();
    // Who had the item when the server's object went (its holder, an owner with a silhouette): the
    // object made again is known to them, so they aren't sent it a second time.
    private static readonly Dictionary<ushort, byte[]> knownBy = new();
    private static float nextScan;

    // The state each holder's game sent as it put an item away (D14, OI-COLD-13), kept as its row's
    // state when the server's object goes: the holder's copy is the item as it was.
    private static readonly Dictionary<ushort, JObject> stashedStates = new();

    /// <summary>Keeps the state a player's game sent with a stash, for the item's row.</summary>
    public static void KeepStashedState(ushort netId, string state)
    {
        if (netId == 0 || string.IsNullOrEmpty(state))
            return;
        try
        {
            stashedStates[netId] = JObject.Parse(state);
        }
        catch (Exception exception)
        {
            Multiplayer.LogWarning($"Inventory: the state sent with netId {netId}'s stash isn't JSON ({exception.Message}); the server's copy is kept");
        }
    }

    private static void ClearDataOnly()
    {
        stashedStates.Clear();
        settlingSince.Clear();
        materializing.Clear();
        knownBy.Clear();
        nextScan = 0;
    }

    /// <summary>Whether the server keeps this item as data only: a live record in a remote inventory, with no object.</summary>
    public static bool IsDataOnly(ushort netId) =>
        netId != 0 && LiveItems.TryGet(netId, out LiveItems.LiveItem record) && IsDataOnly(record);

    public static bool IsDataOnly(LiveItems.LiveItem record) =>
        record != null && record.Placement == ItemPlacementKind.PlayerInventory &&
        ColdInventories.Contains(record.PersistentItemId) &&
        !(NetworkedItem.TryGet(record.NetId, out NetworkedItem item) && item != null);

    /// <summary>The NetId of a container shell kept as data, by its container's id (saved in its state), or 0.</summary>
    public static ushort DataOnlyShell(Guid containerId)
    {
        string wanted = containerId.ToString("D");
        foreach (LiveItems.LiveItem record in LiveItems.Records)
            if (IsDataOnly(record) && ColdInventories.TryGet(record.PersistentItemId, out SavedItemRow row) &&
                string.Equals((string)row.State?["multiplayerColdContainerId"], wanted, StringComparison.OrdinalIgnoreCase))
                return record.NetId;
        return 0;
    }

    /// <summary>Host, every frame: the server's objects for items settled in remote inventories go.</summary>
    public static void Update()
    {
        if (NetworkLifecycle.Instance?.IsHost() != true || Time.realtimeSinceStartup < nextScan)
            return;
        float now = Time.realtimeSinceStartup;
        nextScan = now + ScanSeconds;
        foreach (LiveItems.LiveItem record in LiveItems.Records.ToArray())
        {
            if (!ShouldBeData(record, out NetworkedItem item, out ServerPlayer holder))
            {
                settlingSince.Remove(record.NetId);
                if (record.Placement != ItemPlacementKind.PlayerInventory)
                    stashedStates.Remove(record.NetId);
                continue;
            }
            if (!settlingSince.TryGetValue(record.NetId, out float since))
            {
                settlingSince[record.NetId] = now;
                continue;
            }
            if (now - since < SettleSeconds)
                continue;
            settlingSince.Remove(record.NetId);
            GoData(record, item, holder);
        }
    }

    // A remote player's inventory item whose holder has it: not the host's own player's (the host's
    // game is theirs), not job papers (their job's objects; OI-COLD-07, R6), nothing being made or moved.
    private static bool ShouldBeData(LiveItems.LiveItem record, out NetworkedItem item, out ServerPlayer holder)
    {
        item = null;
        holder = null;
        if (record.Placement != ItemPlacementKind.PlayerInventory || record.Slot < 0 ||
            materializing.Contains(record.NetId) || ItemSaveLoad.IsJobSystemDocument(record.PrefabName) ||
            !NetworkedItem.TryGet(record.NetId, out item) || item == null || item.Item == null ||
            item.ColdMaterializationPending || item.ColdStorageRetirement)
            return false;
        holder = PlayerNumbers.Session(record.Holder);
        return holder != null && holder != NetworkServer.Instance.LocalPlayer && holder.Peer != null &&
               holder.LoadingState >= PlayerLoadingState.ReadyForItems && holder.KnownItems.ContainsKey(item) &&
               !ItemSaveLoad.IsRestorePending(item);
    }

    private static void GoData(LiveItems.LiveItem record, NetworkedItem item, ServerPlayer holder)
    {
        MakeOrdinary(item);
        if (record.PersistentItemId == Guid.Empty)
            return;
        SavedItemRow row = ItemSaveLoad.RowOf(record, item);
        row.PlacementPlayerIdentity = record.Holder;
        row.OwnerIdentity = record.Owner;
        row.Slot = record.Slot;
        if (stashedStates.TryGetValue(record.NetId, out JObject stashed))
            row.State = stashed;
        if (!ColdInventories.Add(row))
            return;
        stashedStates.Remove(record.NetId);

        // The holder keeps theirs, and so does an owner whose silhouette stands for it (recall). Anyone
        // else who was shown it (in the holder's inventory, nearby) is told it's gone.
        List<byte> keeping = new();
        foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
        {
            bool knew = player.KnownItems.Remove(item);
            player.NearbyItems.Remove(item);
            player.AcknowledgedWorldItems.Remove(item.NetId);
            if (!knew)
                continue;
            if (player == holder || player.Guid == record.Owner && record.InventoryClaimSlot >= 0)
            {
                keeping.Add(player.PlayerId);
                continue;
            }
            ItemUpdate destroy = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Destroy);
            if (destroy == null)
                continue;
            LiveItems.WriteToSnapshot(record, destroy, ItemTransitionReason.WentCold);
            destroy.RemovalCause = ItemChangeLog.Removal(ItemRemovalReason.WentCold,
                detail: $"in {holder.Username}'s inventory, kept as data on the server");
            NetworkServer.Instance.SendItemUpdate(destroy, player);
        }
        knownBy[record.NetId] = keeping.ToArray();

        ServerItemManager.Instance?.UnregisterHostWorldItem(item);
        item.HandOverToData();
        StorageIntegration.MoveTo(item.Item, StorageMembership.None);
        Object.Destroy(item.gameObject);
        Multiplayer.LogDebug(() => $"Inventory: {record.PrefabName} (netId {record.NetId}) in {holder.Username}'s slot {record.Slot} is data only on the server");
    }

    // A scene-authored item becomes an ordinary one, so the scene doesn't make it again while it's data.
    private static void MakeOrdinary(NetworkedItem item)
    {
        if (!item.IsSceneAuthored)
            return;
        ItemSaveLoad.RecordDestroyed(item);
        LiveItems.PromoteAuthoredToPersistent(item);
        item.DetachAuthoredItemKey();
    }

    /// <summary>
    /// Has the server's object made again for an item kept as data, under its NetId and in its
    /// holder's inventory, then calls <paramref name="done"/> with it (null if it couldn't be). Any
    /// other item gets its object, or null, straight away.
    /// </summary>
    public static void WithObject(ushort netId, Action<NetworkedItem> done)
    {
        if (!IsDataOnly(netId) && !materializing.Contains(netId))
        {
            done(NetworkedItem.TryGet(netId, out NetworkedItem existing) && existing != null ? existing : null);
            return;
        }
        CoroutineManager.Instance.StartCoroutine(Materialize(netId, done));
    }

    private static IEnumerator Materialize(ushort netId, Action<NetworkedItem> done)
    {
        while (materializing.Contains(netId))
            yield return null;
        if (NetworkedItem.TryGet(netId, out NetworkedItem made) && made != null)
        {
            done(made);
            yield break;
        }
        if (!LiveItems.TryGet(netId, out LiveItems.LiveItem record) ||
            !ColdInventories.TryGet(record.PersistentItemId, out SavedItemRow row))
        {
            done(null);
            yield break;
        }
        InventoryItemSpec spec = Globals.G?.Items?.items?.FirstOrDefault(candidate => candidate != null &&
            string.Equals(candidate.ItemPrefabName, row.PrefabName, StringComparison.Ordinal));
        if (spec == null)
        {
            Multiplayer.LogWarning($"Inventory: no item called {row.PrefabName}; netId {netId} stays data");
            done(null);
            yield break;
        }

        materializing.Add(netId);
        GameObject instance = Object.Instantiate(spec.gameObject,
            StartingItemsController.ITEM_INSTANTIATION_SAFETY_POSITION, Quaternion.identity);
        instance.name = row.PrefabName;
        InventoryItemSpec placed = instance.GetComponent<InventoryItemSpec>();
        if (placed != null && row.OwnerIdentity != Guid.Empty)
            placed.BelongsToPlayer = true;
        NetworkedItem item = instance.GetOrAddComponent<NetworkedItem>();
        item.BeginColdMaterialization();

        // DV's own StartingItemsController waits a frame before loading an item's state.
        yield return null;

        NetworkedItem result = null;
        try
        {
            if (!LiveItems.TryGet(netId, out LiveItems.LiveItem still) || still != record ||
                !ColdInventories.TryGet(record.PersistentItemId, out SavedItemRow current) || current != row)
                throw new InvalidOperationException("it changed meanwhile");
            ItemSaveData saveData = instance.GetComponent<ItemSaveData>();
            if (saveData != null && row.State != null)
            {
                saveData.LoadItemData(row.State);
                saveData.PostLoadItemData();
            }
            ItemBase itemBase = instance.GetComponent<ItemBase>();
            if (itemBase == null || !item.TakeUpFromData(netId))
                throw new InvalidOperationException("couldn't take up its net id");
            ItemIdentity identity = instance.GetComponent<ItemIdentity>() ?? instance.AddComponent<ItemIdentity>();
            identity.Value = record.PersistentItemId;
            item.Initialize(itemBase, netId, true);
            item.FinaliseTrackedValuesAutomatically();
            ColdInventories.Remove(record.PersistentItemId);
            if (LiveItems.TryCreateCorrectionSnapshot(item, out ItemUpdate snapshot))
                item.ApplyServerCanonicalSnapshot(snapshot);
            ServerItemManager.Instance?.RegisterHostWorldItem(item);
            uint tick = NetworkLifecycle.Instance?.Tick ?? 0;
            if (knownBy.TryGetValue(netId, out byte[] knowers))
                foreach (byte playerId in knowers)
                    if (NetworkServer.Instance.TryGetServerPlayer(playerId, out ServerPlayer knower))
                        knower.KnownItems[item] = tick;
            knownBy.Remove(netId);
            item.CompleteColdMaterialization();
            result = item;
        }
        catch (Exception exception)
        {
            item.AbortColdMaterialization();
            Object.Destroy(instance);
            Multiplayer.LogWarning($"Inventory: couldn't make {row.PrefabName} (netId {netId}) from its data: {exception.Message}");
        }
        finally
        {
            materializing.Remove(netId);
        }
        done(result);
    }

    // A data-only item's holder left: it is cold as it is. Whoever still has it (an owner's
    // silhouette) is told, and its record and NetId go.
    private static void DataGoesCold(LiveItems.LiveItem record, ServerPlayer leaver)
    {
        ItemUpdate destroy = new()
        {
            UpdateType = ItemUpdate.ItemUpdateType.Destroy,
            ItemNetId = record.NetId,
            PrefabName = record.PrefabName
        };
        LiveItems.WriteToSnapshot(record, destroy, ItemTransitionReason.WentCold);
        destroy.RemovalCause = ItemChangeLog.Removal(ItemRemovalReason.WentCold, leaver,
            $"slot {record.Slot} of {ItemChangeLog.PlayerName(record.Holder)}, kept as data");
        if (knownBy.TryGetValue(record.NetId, out byte[] knowers))
            foreach (byte playerId in knowers)
                if (playerId != leaver?.PlayerId && NetworkServer.Instance.TryGetServerPlayer(playerId, out ServerPlayer knower))
                    NetworkServer.Instance.SendItemUpdate(destroy, knower);
        knownBy.Remove(record.NetId);
        settlingSince.Remove(record.NetId);
        LiveItems.Remove(record.NetId);
        NetworkedItem.FreeDataNetId(record.NetId);
        Multiplayer.Log($"Item {record.PrefabName} (netId {record.NetId}) removed: {destroy.RemovalCause}");
    }
}
