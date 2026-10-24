using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV;
using DV.CabControls;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Jobs;
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

    // An object made again for a request (WithObject) waits this long before it goes back to data:
    // the request, or the next one (a container browse, then a withdrawal), may still use it.
    private const float RequestGraceSeconds = 1f;
    // How many frames an item the server has just put in an inventory may wait (for vanilla to
    // finish the add, for its holder's game to be ready for items) before it's left an object.
    private const int MaxWaitFrames = 120;

    private sealed class Pending
    {
        public float NotBefore;
        public int Frames;
        // A move the server made (a recall, a Lost and Found retrieval): its holder is sent the row, with its state.
        public bool SendRow;
    }

    // The items the server has just put in (or changed in) a player's inventory while they have an
    // object (Stored): each goes to data in the next frame, or after a request's grace. Nothing
    // scans for them.
    private static readonly Dictionary<ushort, Pending> pendingData = new();
    private static readonly HashSet<ushort> materializing = new();
    // Who had the item when the server's object went (its holder, an owner with a silhouette): the
    // object made again is known to them, so they aren't sent it a second time.
    private static readonly Dictionary<ushort, byte[]> knownBy = new();

    // The state each holder's game sent as it put an item away (D14, OI-COLD-13), laid over the
    // server's own when its object goes: the holder's copy is the item as it was in their hands.
    private static readonly Dictionary<ushort, JObject> stashedStates = new();
    // What only the server keeps in an item's state, which a holder's copy never replaces: a
    // container's id, which every game's copy mints for itself.
    private static readonly HashSet<string> serverKeys = new(StringComparer.Ordinal) { ContainerIdentityPatch.SaveKey };

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
        pendingData.Clear();
        materializing.Clear();
        knownBy.Clear();
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

    /// <summary>
    /// The server has put an item in a player's inventory, or changed it there (a committed
    /// transition, a recall, a Lost and Found retrieval, a swap, an object made for a request):
    /// if it has an object, the object goes in the next frame and the item is data only (D14,
    /// OI-COLD-11). <paramref name="grace"/> keeps an object made for a request a moment longer.
    /// <paramref name="sendRow"/> is a move the server made into the slot (a recall, a Lost and
    /// Found retrieval), whose holder has no state for it: they're sent its row when it goes.
    /// </summary>
    internal static void Stored(LiveItems.LiveItem record, bool grace = false, bool sendRow = false)
    {
        if (record == null || record.Placement != ItemPlacementKind.PlayerInventory || record.NetId == 0 ||
            NetworkLifecycle.Instance?.IsHost() != true || IsDataOnly(record))
            return;
        float notBefore = grace ? Time.realtimeSinceStartup + RequestGraceSeconds : 0f;
        if (!pendingData.TryGetValue(record.NetId, out Pending pending))
            pendingData[record.NetId] = pending = new Pending();
        pending.NotBefore = Math.Max(pending.NotBefore, notBefore);
        pending.SendRow |= sendRow;
    }

    /// <summary>Host, every frame: the server's objects of the items it just put in inventories go to data.</summary>
    public static void Update()
    {
        if (pendingData.Count == 0 || NetworkLifecycle.Instance?.IsHost() != true)
            return;
        float now = Time.realtimeSinceStartup;
        foreach (KeyValuePair<ushort, Pending> entry in pendingData.ToArray())
        {
            if (entry.Value.NotBefore > now)
                continue;
            bool wait = ToData(entry.Key, entry.Value.SendRow);
            if (wait && ++entry.Value.Frames < MaxWaitFrames)
                continue;
            if (wait)
                Multiplayer.LogWarning($"Inventory: netId {entry.Key} is still an object in its holder's inventory after {MaxWaitFrames} frames; it stays one");
            pendingData.Remove(entry.Key);
        }
    }

    // One item the server put in an inventory: its object goes to data now. True to wait a frame
    // (it's being made, or restored, or its holder's game isn't ready for items). Left an object: a
    // loaded magazine (its ammo is an object inside it; OI-CON-16). Job papers go too: their job
    // keeps them by NetId, and they're made again from it (JobPapers; OI-COLD-07).
    private static bool ToData(ushort netId, bool sendRow)
    {
        if (!LiveItems.TryGet(netId, out LiveItems.LiveItem record) || record.Placement != ItemPlacementKind.PlayerInventory ||
            record.Slot < 0 ||
            !NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null || item.Item == null ||
            item.ColdStorageRetirement || InventoryStandIn.HoldsAmmo(item.gameObject))
        {
            // It stays an object (or has gone): its object's state is the item's, not a stash's.
            stashedStates.Remove(netId);
            return false;
        }
        if (materializing.Contains(netId) || item.ColdMaterializationPending || ItemSaveLoad.IsRestorePending(item))
            return true;
        ServerPlayer holder = PlayerNumbers.Session(record.Holder);
        if (holder == null || holder.PlayerId == 0)
            return false;
        if (holder == NetworkServer.Instance.LocalPlayer)
            return !HostInventory.Stow(item, record);
        if (holder.Peer == null)
            return false;
        if (holder.LoadingState < PlayerLoadingState.ReadyForItems)
            return true;
        GoData(record, item, holder, sendRow);
        return false;
    }

    private static void GoData(LiveItems.LiveItem record, NetworkedItem item, ServerPlayer holder, bool sendRow = false)
    {
        MakeOrdinary(item);
        if (record.PersistentItemId == Guid.Empty)
            return;
        SavedItemRow row = ItemSaveLoad.RowOf(record, item);
        row.PlacementPlayerIdentity = record.Holder;
        row.OwnerIdentity = record.Owner;
        row.Slot = record.Slot;
        bool fromHolder = stashedStates.TryGetValue(record.NetId, out JObject stashed);
        if (fromHolder)
            row.State = JobPapers.Complete(item.gameObject, WithHolderValues(row.State, stashed));
        if (!ColdInventories.Add(row))
            return;
        stashedStates.Remove(record.NetId);

        // The holder keeps theirs (it was sent the change that put it there, known or not), and so
        // does an owner whose silhouette stands for it (recall). Anyone else who was shown it (in the
        // holder's inventory, nearby) is told it's gone. So is every other game for a job paper: its
        // job's update made each one a copy when the object was made, near it or not.
        List<byte> keeping = new();
        bool paper = JobPapers.IsPaper(record.PrefabName);
        foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
        {
            bool knew = player.KnownItems.Remove(item);
            player.NearbyItems.Remove(item);
            player.AcknowledgedWorldItems.Remove(item.NetId);
            if (player == holder || knew && player.Guid == record.Owner && record.InventoryClaimSlot >= 0)
            {
                keeping.Add(player.PlayerId);
                continue;
            }
            if (!knew && !(paper && player.Peer != NetworkServer.Instance.SelfPeer &&
                           player.LoadingState >= PlayerLoadingState.ReadyForItems))
                continue;
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
        // A move the server made (a recall, a Lost and Found retrieval) reached the holder as an
        // update with no state: their stand-in is given the row's, so a take-out isn't a fresh item.
        if (sendRow && !fromHolder)
            NetworkServer.Instance.SendInventoryRows(holder, new List<ItemUpdate> { CreateOf(record) },
                new List<string> { row.State?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty });
    }

    // The holder's copy of an item's state over the server's: what changed in their hands (fuel, a
    // label, tape used) is theirs, and what only the server keeps stays the server's.
    private static JObject WithHolderValues(JObject server, JObject holder)
    {
        if (server == null)
        {
            foreach (string key in serverKeys)
                holder.Remove(key);
            return holder;
        }
        JObject merged = (JObject)server.DeepClone();
        foreach (JProperty property in holder.Properties())
            if (!serverKeys.Contains(property.Name))
                merged[property.Name] = property.Value.DeepClone();
        return merged;
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
    /// other item gets its object, or null, straight away. One in the host's own player's slot is
    /// made at once, in place of its stand-in (<see cref="HostInventory.MakeInSlot"/>). An object
    /// still in its inventory after the request goes back to data a moment later (<see cref="Stored"/>).
    /// </summary>
    public static void WithObject(ushort netId, Action<NetworkedItem> done)
    {
        if (!IsDataOnly(netId) && !materializing.Contains(netId))
        {
            done(NetworkedItem.TryGet(netId, out NetworkedItem existing) && existing != null ? existing : null);
            return;
        }
        Action<NetworkedItem> requested = done;
        done = made =>
        {
            requested(made);
            if (made != null && LiveItems.TryGet(netId, out LiveItems.LiveItem record))
                Stored(record, grace: true);
        };
        if (!materializing.Contains(netId) && HostInventory.Holds(netId))
        {
            done(HostInventory.MakeInSlot(netId, "request"));
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
        GameObject instance = InstantiateFromRow(netId, row, out NetworkedItem item);
        if (instance == null)
        {
            done(null);
            yield break;
        }

        // DV's own StartingItemsController waits a frame before loading an item's state.
        yield return null;

        NetworkedItem result = null;
        try
        {
            if (!LiveItems.TryGet(netId, out LiveItems.LiveItem still) || still != record ||
                !ColdInventories.TryGet(record.PersistentItemId, out SavedItemRow current) || current != row)
                throw new InvalidOperationException("it changed meanwhile");
            result = TakeUpObject(record, row, item, null);
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

    /// <summary>
    /// The server's object for an item kept as data, made in this frame with its state (the host's
    /// own take-out, D14), under its NetId and parked as a stashed item is: not placed anywhere, for
    /// the caller to put in the slot. Null if it couldn't be made.
    /// </summary>
    internal static NetworkedItem MakeNow(ushort netId, ServerPlayer holder)
    {
        if (materializing.Contains(netId) || NetworkedItem.TryGet(netId, out NetworkedItem existing) && existing != null ||
            !LiveItems.TryGet(netId, out LiveItems.LiveItem record) ||
            !ColdInventories.TryGet(record.PersistentItemId, out SavedItemRow row))
            return null;
        GameObject instance = InstantiateFromRow(netId, row, out NetworkedItem item);
        if (instance == null)
            return null;
        try
        {
            return TakeUpObject(record, row, item, holder);
        }
        catch (Exception exception)
        {
            item.AbortColdMaterialization();
            Object.Destroy(instance);
            Multiplayer.LogWarning($"Inventory: couldn't make {row.PrefabName} (netId {netId}) from its data: {exception.Message}");
            return null;
        }
        finally
        {
            materializing.Remove(netId);
        }
    }

    // Its prefab made at DV's safety position, as a cold item being made: nothing observes it yet.
    private static GameObject InstantiateFromRow(ushort netId, SavedItemRow row, out NetworkedItem item)
    {
        item = null;
        if (JobPapers.IsPaper(row.PrefabName))
        {
            Component paper = JobPapers.Make(row.PrefabName, row.State,
                StartingItemsController.ITEM_INSTANTIATION_SAFETY_POSITION, Quaternion.identity);
            if (paper == null)
            {
                Multiplayer.LogWarning($"Inventory: {row.PrefabName} netId {netId} has no job (or recipe) to be made from; it stays data");
                return null;
            }
            materializing.Add(netId);
            item = paper.GetOrAddComponent<NetworkedItem>();
            item.BeginColdMaterialization();
            return paper.gameObject;
        }
        InventoryItemSpec spec = Globals.G?.Items?.items?.FirstOrDefault(candidate => candidate != null &&
            string.Equals(candidate.ItemPrefabName, row.PrefabName, StringComparison.Ordinal));
        if (spec == null)
        {
            Multiplayer.LogWarning($"Inventory: no item called {row.PrefabName}; netId {netId} stays data");
            return null;
        }
        materializing.Add(netId);
        GameObject instance = ItemStateText.Instantiate(spec.gameObject,
            StartingItemsController.ITEM_INSTANTIATION_SAFETY_POSITION, Quaternion.identity);
        instance.name = row.PrefabName;
        InventoryItemSpec placed = instance.GetComponent<InventoryItemSpec>();
        if (placed != null && row.OwnerIdentity != Guid.Empty)
            placed.BelongsToPlayer = true;
        item = instance.GetOrAddComponent<NetworkedItem>();
        item.BeginColdMaterialization();
        return instance;
    }

    // Its state loaded and its NetId taken up; its row goes and whoever knew it knows the object.
    // A remote holder's is placed as the server keeps it; the host's own (holder) is left parked,
    // its record's metadata on it, for the host's slot.
    private static NetworkedItem TakeUpObject(LiveItems.LiveItem record, SavedItemRow row, NetworkedItem item,
        ServerPlayer holder)
    {
        GameObject instance = item.gameObject;
        ushort netId = record.NetId;
        ItemStateText.LoadNowOrSoon(instance, row.State, $"{row.PrefabName} (netId {netId})");
        ItemBase itemBase = instance.GetComponent<ItemBase>();
        if (itemBase == null || !item.TakeUpFromData(netId))
            throw new InvalidOperationException("couldn't take up its net id");
        ItemIdentity identity = instance.GetComponent<ItemIdentity>() ?? instance.AddComponent<ItemIdentity>();
        identity.Value = record.PersistentItemId;
        if (JobPapers.PaperOf(instance) is { } paper)
        {
            if (JobPapers.Track(item, paper, netId, true, row.State) is { } report)
                Jobs.JobReportArtifactRegistry.Register(report);
        }
        else
            item.Initialize(itemBase, netId, true);
        item.FinaliseTrackedValuesAutomatically();
        ColdInventories.Remove(record.PersistentItemId);
        if (LiveItems.TryCreateCorrectionSnapshot(item, out ItemUpdate snapshot))
        {
            if (holder != null)
                item.ApplyHostLocalCanonicalTransition(snapshot, holder);
            else
                item.ApplyServerCanonicalSnapshot(snapshot);
        }
        if (holder == null)
            ServerItemManager.Instance?.RegisterHostWorldItem(item);
        uint tick = NetworkLifecycle.Instance?.Tick ?? 0;
        if (knownBy.TryGetValue(netId, out byte[] knowers))
            foreach (byte playerId in knowers)
                if (NetworkServer.Instance.TryGetServerPlayer(playerId, out ServerPlayer knower))
                    knower.KnownItems[item] = tick;
        knownBy.Remove(netId);
        item.CompleteColdMaterialization();
        return item;
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
        pendingData.Remove(record.NetId);
        if (JobPapers.IsPaper(record.PrefabName))
            Jobs.JobPaperRows.Forget(record.NetId);
        LiveItems.Remove(record.NetId);
        NetworkedItem.FreeDataNetId(record.NetId);
        Multiplayer.Log($"Item {record.PrefabName} (netId {record.NetId}) removed: {destroy.RemovalCause}");
    }
}
