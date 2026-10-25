using DV;
using DV.CabControls;
using DV.Utils;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Gadgets;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Trains;
using Multiplayer.Debugging.Protocol;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Server.Items.SaveLoad;

/// <summary>
/// Making an item's object from its saved row (docs/design/items/save-load.md, "Loading"): with its
/// id, in its home, its state loaded once the object has woken. A state that won't load comes back
/// fresh, with the same id, in its owner's Lost and Found (D9, OI-LED-16).
/// </summary>
internal static partial class ItemSaveLoad
{
    private const int StateAttempts = 5;

    private static readonly List<PendingStateRestore> pendingStateRestores = new();
    private static readonly HashSet<NetworkedItem> pendingRepresentations = new();

    private sealed class PendingStateRestore
    {
        public NetworkedItem Item;
        public Guid Id;
        public JObject State;
        public string Prefab;
        public int Attempts;
        public float NextAttemptAt;
    }

    private static void ClearMake()
    {
        pendingStateRestores.Clear();
        pendingRepresentations.Clear();
    }

    /// <summary>Its state is still being loaded: nobody is sent it yet.</summary>
    public static bool IsRestorePending(NetworkedItem item) => item != null && pendingRepresentations.Contains(item);

    /// <summary>Whether an item of this prefab can be made here: from its spec, or a job paper from its job.</summary>
    internal static bool PrefabKnown(string prefabName) =>
        FindSpec(prefabName) != null || Shared.Jobs.JobPapers.IsPaper(prefabName);

    internal static InventoryItemSpec FindSpec(string prefabName) =>
        string.IsNullOrEmpty(prefabName) ? null : Globals.G?.Items?.items?.FirstOrDefault(candidate =>
            candidate != null && string.Equals(candidate.ItemPrefabName, prefabName, StringComparison.Ordinal));

    /// <summary>
    /// Makes the item loose where its row says: the world, a car's interior, a shelf, a snap point, a
    /// car's end. <paramref name="owned"/> marks it a player's, as vanilla's own flag does. False when
    /// its prefab (or, for a scene item, its authored object) is gone.
    /// </summary>
    private static bool MakeLive(SavedItemRow row, bool owned) => MakeLive(row, owned, out _);

    /// <summary>The same, outside the load (a withdrawal with no slot free): <paramref name="made"/> is its record.</summary>
    internal static bool MakeLive(SavedItemRow row, bool owned, out LiveItems.LiveItem made)
    {
        made = null;
        // Ammo goes into its live tool's magazine (containers.md, "Ammo in a magazine").
        ushort toolNetId = row.Placement == ItemPlacementKind.Magazine ? LiveToolNetId(row.ContainerId) : (ushort)0;
        if (row.Placement == ItemPlacementKind.Magazine && toolNetId == 0)
        {
            Multiplayer.LogWarning($"Saved item {row.PrefabName} ({row.PersistentItemId:D}) can't be made: its tool {row.ContainerId:D} isn't live");
            return false;
        }
        NetworkedItem item = FindOrCreate(row);
        if (item == null)
            return false;
        if (owned && item.Item?.InventorySpecs != null)
            item.Item.InventorySpecs.BelongsToPlayer = true;
        // A scene item installed as a gadget is vanilla's installed storage's to place.
        if (GadgetItemTransitions.IsInstalled(item))
        {
            Multiplayer.LogWarning($"Ignoring saved item {row.PrefabName} ({row.PersistentItemId:D}): it is an installed gadget");
            return false;
        }

        item.transform.SetPositionAndRotation(row.Position + WorldMover.currentMove, row.Rotation);
        ItemUpdate seed = item.CreateUpdateData(ItemUpdate.ItemUpdateType.ItemState) ?? new ItemUpdate();
        seed.ItemState = row.Placement is ItemPlacementKind.Attached or ItemPlacementKind.SnappedAttachment
            ? ItemState.Attached
            : row.Placement == ItemPlacementKind.Magazine ? ItemState.InMagazine : ItemState.Dropped;
        seed.MagazineNetId = toolNetId;
        seed.MagazineSlot = row.Placement == ItemPlacementKind.Magazine ? row.Slot : -1;
        seed.PlayerId = 0;
        seed.ItemPosition = row.Position;
        seed.ItemRotation = row.Rotation;
        // On a gadget's snap point: the gadget by its item's id, now live under a NetId of its own.
        if (row.Placement == ItemPlacementKind.SnappedAttachment)
        {
            seed.WorldParentKind = ItemWorldParentKind.SnapPoint;
            seed.WorldParentKey = row.WorldParentKey ?? string.Empty;
            seed.WorldParentNetId = GadgetNetIdOf(row.WorldParentKey);
        }
        LiveItems.LiveItem record = made = LiveItems.Ensure(item, null, seed);
        if (record == null)
            return false;
        ApplySavedRecord(record, row);
        LiveItems.WriteToSnapshot(record, seed, ItemTransitionReason.PersistenceRestore);
        item.ApplyServerCanonicalSnapshot(seed);
        if (row.Placement == ItemPlacementKind.SnappedAttachment && GadgetSnapPoints.PointOf(seed.WorldParentNetId) == null)
            SingletonBehaviour<CoroutineManager>.Instance.StartCoroutine(SnapWhenItsGadgetIs(item, record));
        if (row.State != null)
            DeferItemStateRestore(item, row);
        else
            ServerItemManager.Instance?.RegisterHostWorldItem(item);
        return true;
    }

    /// <summary>
    /// Makes an owned item in its owner's Lost and Found: from its row, or fresh from its prefab when
    /// there's none (D9). Its entry is a reason and a time; its claim is the ledger's.
    /// </summary>
    private static bool MakeInLostAndFound(Guid id, string prefab, SavedItemRow row, Guid owner,
        LostItemReason reason, long lostUtcTicks, string displayName, ItemInventoryClaimFlags claimFlags)
    {
        SavedItemRow made = row ?? new SavedItemRow { PersistentItemId = id, PrefabName = prefab };
        made.Position = StartingItemsController.ITEM_INSTANTIATION_SAFETY_POSITION - WorldMover.currentMove;
        made.Rotation = Quaternion.identity;
        NetworkedItem item = FindOrCreate(made);
        if (item == null)
            return false;
        if (item.Item?.InventorySpecs != null)
            item.Item.InventorySpecs.BelongsToPlayer = true;
        if (!LostAndFoundServer.RestoreEntry(item, owner, reason, lostUtcTicks, displayName,
                ServerLedger.ClaimOf(id), claimFlags))
        {
            Multiplayer.LogError($"item.load-lost-and-found-refused: {prefab} ({id:D}) couldn't go into " +
                                 $"{ItemChangeLog.PlayerName(owner)}'s Lost and Found; it is loose at the safety position");
            return false;
        }
        if (made.State != null)
            DeferItemStateRestore(item, made);
        return true;
    }

    private static NetworkedItem FindOrCreate(SavedItemRow row)
    {
        NetworkedItem existing = FindExisting(row.PersistentItemId, row.AuthoredItemKey);
        if (existing != null || row.PersistentItemId == Guid.Empty)
            return existing;
        if (Shared.Jobs.JobPapers.IsPaper(row.PrefabName))
            return MakePaper(row);
        InventoryItemSpec spec = FindSpec(row.PrefabName);
        if (spec == null)
            return null;
        GameObject instance = Object.Instantiate(spec.gameObject, row.Position + WorldMover.currentMove, row.Rotation);
        instance.name = row.PrefabName;
        ItemIdentity identity = instance.GetComponent<ItemIdentity>() ?? instance.AddComponent<ItemIdentity>();
        identity.Value = row.PersistentItemId;
        return instance.GetComponent<NetworkedItem>() ?? instance.AddComponent<NetworkedItem>();
    }

    // A job paper is made from its job, and a report from its recipe, never from its prefab, which
    // would have nothing printed on it (OI-COLD-07). Null if its job or recipe is gone.
    private static NetworkedItem MakePaper(SavedItemRow row)
    {
        Component paper = Shared.Jobs.JobPapers.Make(row.PrefabName, row.State, row.Position + WorldMover.currentMove, row.Rotation);
        if (paper == null)
            return null;
        ItemIdentity identity = paper.GetComponent<ItemIdentity>() ?? paper.gameObject.AddComponent<ItemIdentity>();
        identity.Value = row.PersistentItemId;
        NetworkedItem item = paper.GetComponent<NetworkedItem>() ?? paper.gameObject.AddComponent<NetworkedItem>();
        if (Shared.Jobs.JobPapers.Track(item, paper, 0, true, row.State) is { } report)
            Jobs.JobReportArtifactRegistry.Register(report);
        return item;
    }

    /// <summary>The NetId of the live object of the item with this id (a tool whose magazine ammo goes into), or 0.</summary>
    internal static ushort LiveToolNetId(Guid toolId) => toolId == Guid.Empty ? (ushort)0
        : LiveItems.GetAll().FirstOrDefault(record => record.PersistentItemId == toolId &&
              record.Placement is not ItemPlacementKind.Destroyed && !PlayerItems.IsDataOnly(record) &&
              NetworkedItem.TryGet(record.NetId, out NetworkedItem tool) && tool != null)?.NetId ?? 0;

    private static NetworkedItem FindExisting(Guid persistentId, string authoredKey)
    {
        if (WorldItemStableIdentity.IsValid(authoredKey) &&
            NetworkedItemManager.Instance.Authored.TryGet(authoredKey, out NetworkedItem authored))
            return authored;
        if (persistentId == Guid.Empty)
            return null;
        return NetworkedItem.GetAll().Where(item => item != null).Distinct().FirstOrDefault(item =>
            item.GetComponent<ItemIdentity>()?.Value == persistentId);
    }

    private static void ApplySavedRecord(LiveItems.LiveItem record, SavedItemRow saved)
    {
        record.Revision = saved.Revision;
        record.Placement = saved.Placement;
        record.Holder = Guid.Empty;
        record.PlacementHand = PlayerHand.None;
        record.Slot = -1;
        record.Position = saved.Position;
        record.Rotation = saved.Rotation;
        record.LastReason = ItemTransitionReason.PersistenceRestore;
        // Kept, not logged: a load makes every saved item at once.
        record.LastMove = ItemChangeLog.Move(ItemMoveReason.RestoredFromSave, detail: "saved as " + saved.Placement);
        record.PersistentItemId = saved.PersistentItemId;
        record.AuthoredItemKey = saved.AuthoredItemKey;
        record.WorldParentKind = saved.WorldParentKind;
        record.WorldParentNetId = 0;
        record.WorldParentKey = saved.WorldParentKey;
        record.WorldParentPersistentId = saved.WorldParentPersistentId;
        if (record.WorldParentKind == ItemWorldParentKind.TrainInterior &&
            TryResolveTrain(record.WorldParentPersistentId, out TrainCar parentCar))
            NetworkedTrainCar.TryGetNetId(parentCar, out record.WorldParentNetId);
        record.AttachedCarPersistentId = saved.AttachedCarPersistentId;
        record.AttachedFront = saved.AttachedFront;
        if (record.Placement == ItemPlacementKind.Attached &&
            TryResolveTrain(record.AttachedCarPersistentId, out TrainCar attachedCar))
            NetworkedTrainCar.TryGetNetId(attachedCar, out record.AttachedCarNetId);
        if (record.Placement == ItemPlacementKind.SnappedAttachment)
        {
            record.WorldParentKind = ItemWorldParentKind.SnapPoint;
            record.WorldParentNetId = GadgetNetIdOf(record.WorldParentKey);
        }
        record.ParentLocalPosition = saved.ParentLocalPosition;
        record.ParentLocalRotation = saved.ParentLocalRotation;
        bool inMagazine = record.Placement == ItemPlacementKind.Magazine;
        record.MagazineToolId = inMagazine ? saved.ContainerId : Guid.Empty;
        record.MagazineNetId = inMagazine ? LiveToolNetId(saved.ContainerId) : (ushort)0;
        record.MagazineSlot = inMagazine ? saved.Slot : -1;
        record.HasPersistentOverride = saved.HasPersistentOverride;
        record.IsReplenishableStockFork = saved.IsReplenishableStockFork;
    }

    // Vanilla loads an item's state a frame after making it (StartingItemsController): until then
    // the item isn't sent to anyone.
    private const float SnapPointWaitSeconds = 30f;

    private static ushort GadgetNetIdOf(string snapPointKey) =>
        SnapPointKey.TryGadget(snapPointKey, out Guid gadget) ? ServerLedger.NetIdOf(gadget) : (ushort)0;

    // A snapped item made before its gadget is waits for it (live-state.md, "Attached and snapped
    // items"); a gadget that doesn't come sends it to its owner's Lost and Found, as a gone car does.
    private static IEnumerator SnapWhenItsGadgetIs(NetworkedItem item, LiveItems.LiveItem record)
    {
        float until = Time.realtimeSinceStartup + SnapPointWaitSeconds;
        ushort gadget = 0;
        while (item != null && record.Placement == ItemPlacementKind.SnappedAttachment &&
               GadgetSnapPoints.PointOf(gadget = GadgetNetIdOf(record.WorldParentKey)) == null &&
               Time.realtimeSinceStartup < until)
            yield return new WaitForSecondsRealtime(0.5f);
        if (item == null || record.Placement != ItemPlacementKind.SnappedAttachment)
            yield break;
        if (GadgetSnapPoints.PointOf(gadget) == null)
        {
            Multiplayer.LogWarning($"Saved item {record.PrefabName} ({record.PersistentItemId:D}) is snapped to {record.WorldParentKey}, " +
                                   $"which isn't here after {SnapPointWaitSeconds} s");
            // Owned, to its owner's Lost and Found; unowned (or refused), into the world where it is,
            // as off a deleted car: never left snapped to nothing.
            if (record.Owner == Guid.Empty || !ServerLedger.SendToLostAndFound(record.PersistentItemId,
                    LostItemReason.SaveRecovery, "its snap point's gadget is gone", out _))
                DeletedCarItems.IntoWorld(item, record, "its snap point's gadget is gone");
            yield break;
        }
        record.WorldParentNetId = gadget;
        ItemUpdate snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.ItemState);
        if (snapshot == null)
            yield break;
        LiveItems.WriteToSnapshot(record, snapshot, ItemTransitionReason.PersistenceRestore);
        snapshot.ItemState = ItemState.Attached;
        item.ApplyServerCanonicalSnapshot(snapshot);
        snapshot.OriginatingPlayerId = 0;
        Session.NetworkServer.Instance?.SendItemUpdate(snapshot);
    }

    private static void DeferItemStateRestore(NetworkedItem item, SavedItemRow saved)
    {
        pendingRepresentations.Add(item);
        ServerItemManager.Instance?.UnregisterHostWorldItem(item);
        pendingStateRestores.Add(new PendingStateRestore
        {
            Item = item,
            Id = saved.PersistentItemId,
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
            if (now >= pending.NextAttemptAt)
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
            RemovePendingStateRestore(pending);
            if (!LostAndFoundServer.Contains(pending.Item.NetId))
                ServerItemManager.Instance?.RegisterHostWorldItem(pending.Item);
            DebugRuntime.Publish("item-world", "world-item.persistence-state-restored", DebugRuntimeSide.Server,
                entityType: "Item", entityId: pending.Item.NetId.ToString(), data: new Dictionary<string, object>
                {
                    ["prefabName"] = pending.Prefab ?? string.Empty,
                    ["attempts"] = pending.Attempts + 1
                });
        }
        catch (Exception ex)
        {
            pending.Attempts++;
            if (pending.Attempts < StateAttempts)
            {
                pending.NextAttemptAt = now + 0.25f * pending.Attempts;
                return;
            }
            FailPendingStateRestore(pending, ex);
        }
    }

    // An owned item whose state won't load comes back fresh in its owner's Lost and Found (D9); an
    // unowned one stays as it is, with what of its state loaded.
    private static void FailPendingStateRestore(PendingStateRestore pending, Exception error)
    {
        RemovePendingStateRestore(pending);
        DebugRuntime.Publish("item-world", "world-item.persistence-state-restore-failed",
            DebugRuntimeSide.Server, DebugSeverity.Error, "Item", pending.Item.NetId.ToString(),
            new Dictionary<string, object>
            {
                ["prefabName"] = pending.Prefab ?? string.Empty,
                ["attempts"] = pending.Attempts,
                ["error"] = error.Message
            });
        if (pending.Id != Guid.Empty && ServerLedger.Ledger.IsLive(pending.Id))
        {
            SingletonBehaviour<CoroutineManager>.Instance.StartCoroutine(
                RemakeFresh(pending.Item, pending.Id, pending.Prefab, error.Message));
            return;
        }
        Multiplayer.LogError($"item.state-unloadable: {pending.Prefab}'s saved state won't load: {error.Message}");
        if (!LostAndFoundServer.Contains(pending.Item.NetId))
            ServerItemManager.Instance?.RegisterHostWorldItem(pending.Item);
    }

    private static void RemovePendingStateRestore(PendingStateRestore pending)
    {
        pendingStateRestores.Remove(pending);
        pendingRepresentations.Remove(pending.Item);
    }

    /// <summary>
    /// The item's saved state won't load (OI-LED-16): its object goes, and it is made again fresh
    /// from its prefab, with the same id, in its owner's Lost and Found (<c>SaveRecovery</c>).
    /// </summary>
    internal static IEnumerator RemakeFresh(NetworkedItem broken, Guid id, string prefab, string error)
    {
        // An installed gadget's item is the gadget code's (OI-GAD-06): it stays, and the refusal
        // is logged.
        if (broken != null && InstalledItems.RefuseDestroy(broken, "remade fresh: " + error))
            yield break;
        Guid owner = ServerLedger.OwnerOf(id);
        Multiplayer.LogError($"item.state-unloadable: {prefab} ({id:D})'s saved state won't load ({error}); " +
                             $"made again fresh, with the same id, in {ItemChangeLog.PlayerName(owner)}'s Lost and Found");
        if (broken != null)
        {
            if (LostAndFoundServer.Contains(broken.NetId))
                LostAndFoundServer.Forget(broken.NetId, "its item is remade fresh");
            broken.NoteRemovalCause(ItemChangeLog.Removal(ItemRemovalReason.RemadeFresh, detail: error));
            Object.Destroy(broken.gameObject);
            // The old object's record and id go with it before the new one takes the id.
            yield return null;
            yield return null;
        }
        MakeInLostAndFound(id, prefab, null, owner, LostItemReason.SaveRecovery, DateTime.UtcNow.Ticks,
            null, ItemInventoryClaimFlags.None);
    }

    /// <summary>
    /// An owned item with no copy anywhere (the one-home repair, D7): made again fresh from its prefab,
    /// with the same id, in its owner's Lost and Found (<c>SaveRecovery</c>), as an unloadable one is (D9).
    /// </summary>
    internal static bool MakeLostAgain(Guid id, string prefab, Guid owner) =>
        // Only an item with no object anywhere: one found by its id is a copy, which the repair re-mints.
        FindExisting(id, null) == null &&
        MakeInLostAndFound(id, prefab, null, owner, LostItemReason.SaveRecovery, DateTime.UtcNow.Ticks,
            null, ItemInventoryClaimFlags.None);

    private static bool RestoreAuthoredTombstone(SavedItemRow saved, JObject source)
    {
        if (!WorldItemStableIdentity.IsValid(saved.AuthoredItemKey) ||
            !NetworkedItemManager.Instance.Authored.TryGet(saved.AuthoredItemKey, out NetworkedItem item) ||
            item == null)
            return false;

        ServerItemManager.Instance?.UnregisterHostWorldItem(item);
        item.gameObject.SetActive(false);
        ReplaceAuthoredTombstone((JObject)source.DeepClone());

        // Canonicalize the tombstone before the first dirty scan can rediscover the authored object.
        LiveItems.LiveItem record = LiveItems.Ensure(item);
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

    internal static bool TryResolveTrain(string carGuid, out TrainCar trainCar)
    {
        trainCar = null;
        if (string.IsNullOrEmpty(carGuid))
            return false;
        trainCar = Resources.FindObjectsOfTypeAll<TrainCar>().FirstOrDefault(candidate =>
            candidate != null && string.Equals(candidate.CarGUID, carGuid, StringComparison.Ordinal));
        return trainCar != null;
    }
}
