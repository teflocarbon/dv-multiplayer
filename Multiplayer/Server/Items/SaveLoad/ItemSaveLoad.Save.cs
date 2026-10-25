using DV.CabControls;
using DV.ThingTypes;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Inventory;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Players;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items.SaveLoad;

/// <summary>
/// The save (docs/design/items/save-load.md, "The key"): every item in one pass, read from the
/// ledger and live state together. An owned item's owner, home and claim are its ledger entry; its
/// state is one row, wherever its copy is (an object, a cold row, a container's row, Lost and
/// Found's object). Then vanilla's lists, written as a mirror from the same rows (D2).
/// </summary>
internal static partial class ItemSaveLoad
{
    public static void WriteSave(JObject multiplayerRoot, SaveGameData data)
    {
        if (multiplayerRoot == null || NetworkLifecycle.Instance?.IsHost() != true)
            return;
        // Before the load, the save's own key stays as it was.
        if (!loaded)
            return;
        if (newerKey != null)
        {
            multiplayerRoot[ItemsSaveCodec.Key] = newerKey.DeepClone();
            return;
        }

        PromoteSceneItemsInInventories();
        SaveContext context = new();
        WriteOwned(context);
        WriteUnowned(context);
        WriteContainers(context);
        context.Items.Containers.AddRange(kept.Containers.Select(row => row.DeepClone()));
        context.Items.UnreadableLedger.AddRange(kept.Ledger.Select(row => row.DeepClone()));
        foreach (KeyValuePair<string, JToken> state in kept.States)
            context.Items.UnreadableStates[state.Key] = state.Value.DeepClone();
        context.Items.UnreadableLostAndFound.AddRange(kept.LostAndFound.Select(row => row.DeepClone()));
        context.Items.StarterKits.AddRange(ServerLedger.Ledger.StarterKitsGiven);

        multiplayerRoot[ItemsSaveCodec.Key] = ItemsSaveCodec.Write(context.Items);
        // Not written again (D1).
        foreach (string old in ItemsSaveCodec.OldKeys)
            multiplayerRoot.Remove(old);
        WriteVanillaMirror(data, context);
    }

    // Every container of the graph, but an empty one whose shell is gone (F7): an item with a
    // container keeps a row once it has a keeper, and nothing else ever drops one whose shell went
    // without a box's destruction (an unowned item gone, a refused deposit's). An empty container
    // is made again from its shell as the shell binds, so leaving one out loses nothing.
    private static void WriteContainers(SaveContext context)
    {
        ContainerGraphSnapshot graph = ContainerServer.Graph.ExportSnapshot();
        HashSet<Guid> holding = new(graph.Items.Select(row => row.ParentContainerId));
        foreach (JToken row in context.Items.Unowned)
            if (row is JObject unowned && Guid.TryParse((string)unowned["container"], out Guid container))
                holding.Add(container);
        foreach (LedgerEntry entry in context.Items.Ledger)
            if (entry.Home.Kind == HomeKind.Container && Guid.TryParse(entry.Home.Anchor, out Guid container))
                holding.Add(container);
        HashSet<Guid> items = new(graph.Items.Select(row => row.PersistentItemId));
        foreach (LiveItems.LiveItem record in LiveItems.GetAll())
            if (record.PersistentItemId != Guid.Empty && record.Placement != ItemPlacementKind.Destroyed)
                items.Add(record.PersistentItemId);
        int dropped = 0;
        foreach (ContainerRecord container in graph.Containers)
        {
            Guid id = container.PersistentContainerId;
            if (!holding.Contains(id) && !items.Contains(id) && !ServerLedger.Ledger.IsLive(id) &&
                !ColdInventories.Contains(id) && !PlayerItems.TryGetAmmoRow(id, out _))
            {
                dropped++;
                continue;
            }
            context.Items.Containers.Add(ItemsSaveCodec.WriteContainer(container));
        }
        if (dropped > 0)
            Multiplayer.Log($"Items: {dropped} empty container(s) whose items are gone aren't saved");
    }

    private sealed class SaveContext
    {
        public ItemsSave Items { get; } = new();
        /// <summary>Slots given to items saved from a hand, so two hands' items don't share one.</summary>
        public HashSet<(Guid Holder, int Slot)> HandSlots { get; } = new();
        /// <summary>The host's own player's inventory, slot by slot, for vanilla's mirror.</summary>
        public List<StorageItemData> Mirror { get; } = new();
        public Guid MirrorHolder { get; } = NetworkLifecycle.Instance?.IsDedicatedHost == true ? Guid.Empty : PlayerIdentity.Local;
    }

    // An owned item: its entry, its state under its id, and its Lost and Found row.
    private static void WriteOwned(SaveContext context)
    {
        ItemsSave items = context.Items;
        Dictionary<Guid, (LiveItems.LiveItem Record, NetworkedItem Item)> live = LiveCopies();
        Dictionary<Guid, StoredItemRecord> stored = ContainerServer.Graph.ExportSnapshot().Items
            .Where(row => row.PersistentItemId != Guid.Empty)
            .GroupBy(row => row.PersistentItemId).ToDictionary(group => group.Key, group => group.First());
        int noState = 0;
        foreach (LedgerEntry entry in ServerLedger.Ledger.All())
        {
            // The job system saves its jobs and prints their paperwork again at the load; a booklet
            // saved as well came back beside the reprint, one more each load. A player's own is
            // theirs (OI-COLD-15), and the reprint waits for it (JobBookletRecoveryPatch).
            if (IsUnsavedJobPaper(entry))
                continue;
            if (!entry.IsLive)
            {
                items.Ledger.Add(entry);
                continue;
            }
            if (entry.Home.Kind == HomeKind.Unloadable)
            {
                if (keptOwned.TryGetValue(entry.Id, out KeptItem keptItem))
                {
                    items.Ledger.Add(entry);
                    items.LedgerRows[entry.Id] = (JObject)keptItem.Ledger.DeepClone();
                    if (keptItem.State != null)
                        items.States[entry.Id] = (JObject)keptItem.State.DeepClone();
                    if (keptItem.LostAndFound != null)
                        items.LostAndFound[entry.Id] = (JObject)keptItem.LostAndFound.DeepClone();
                }
                continue;
            }

            SavedItemRow row = OwnedRow(entry.Id, live, stored);
            LedgerEntry saved = entry;
            LostItemRecord lost = null;
            if (entry.Home.Kind == HomeKind.Hand)
            {
                // Never saved in a hand: in a free slot of the holder's, or their Lost and Found.
                int slot = SaveSlot(entry.Id, entry.Owner, entry.ClaimSlot, entry.Home.Holder, context);
                saved = WithHome(entry, slot >= 0 ? Home.InSlot(entry.Home.Holder, slot) : Home.LostAndFound);
                if (slot < 0)
                    lost = new LostItemRecord { PersistentItemId = entry.Id, Reason = LostItemReason.InventoryOverflow, LostUtcTicks = DateTime.UtcNow.Ticks };
            }
            items.Ledger.Add(saved);
            // An installed gadget's state is vanilla's installed storage's.
            if (entry.Home.Kind == HomeKind.Installed)
                continue;
            if (row == null)
            {
                noState++;
                Multiplayer.LogWarning($"Items: {entry.Prefab} ({entry.Id:D}) is {entry.Home} but has no copy to save; " +
                                       "it is made fresh in Lost and Found at the next load");
                continue;
            }
            items.States[entry.Id] = row.ToJson(owned: true);
            if (saved.Home.Kind == HomeKind.LostAndFound &&
                (lost != null || LostAndFoundServer.TryGetEntry(entry.Id, out lost)))
                items.LostAndFound[entry.Id] = ItemsSaveCodec.WriteLost(lost);
            if (saved.Home.Kind == HomeKind.Slot && saved.Home.Holder == context.MirrorHolder)
                context.Mirror.Add(MirrorItem(row, saved.Home.Slot, owned: true));
        }
        if (noState > 0)
            Multiplayer.LogError($"Items: {noState} owned item(s) had no copy to save");
    }

    // Each unowned item worth keeping: one a player carries, a player's item lying about, a scene
    // item moved from where the scene has it (an override) or gone (a tombstone).
    private static void WriteUnowned(SaveContext context)
    {
        ItemsSave items = context.Items;
        foreach (LiveItems.LiveItem record in LiveItems.GetAll().ToArray())
        {
            if (record.PersistentItemId != Guid.Empty && ServerLedger.Ledger.IsLive(record.PersistentItemId))
                continue;
            if (!NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item?.Item == null ||
                PlayerItems.IsDataOnly(record))
                continue;
            if (WorldItemStableIdentity.IsValid(record.AuthoredItemKey))
            {
                if (AuthoredMoved(record))
                    items.Authored.Add(RowOf(record, item).ToJson(owned: false));
                continue;
            }
            if (!ShouldPersistUnowned(record, item))
                continue;
            SavedItemRow row = RowOf(record, item);
            if (row.Placement == ItemPlacementKind.PlayerHand)
            {
                int slot = SaveSlot(record.PersistentItemId, OwnerRef.None, -1, record.Holder, context);
                if (slot < 0)
                {
                    // No slot for it: saved where it was taken from, the world at the holder's feet.
                    row.Placement = ItemPlacementKind.World;
                    row.PlacementPlayerIdentity = Guid.Empty;
                }
                else
                {
                    row.Placement = ItemPlacementKind.PlayerInventory;
                    row.Slot = slot;
                }
            }
            items.Unowned.Add(row.ToJson(owned: false));
            if (row.Placement == ItemPlacementKind.PlayerInventory && row.PlacementPlayerIdentity == context.MirrorHolder)
                context.Mirror.Add(MirrorItem(row, row.Slot, owned: false));
        }
        // An offline player's unowned items, kept as data (D12).
        foreach (SavedItemRow row in ColdInventories.All())
            if (!ServerLedger.Ledger.IsLive(row.PersistentItemId))
                items.Unowned.Add(row.ToJson(owned: false));
        // Unowned ammo in a tool kept as data, kept as data with it (OI-CON-16).
        foreach (SavedItemRow row in PlayerItems.AmmoRows())
            if (!ServerLedger.Ledger.IsLive(row.PersistentItemId))
                items.Unowned.Add(row.ToJson(owned: false));
        // A container's items the ledger doesn't hold: their container is their home.
        foreach (StoredItemRecord stored in ContainerServer.Graph.ExportSnapshot().Items)
            if (!ServerLedger.Ledger.IsLive(stored.PersistentItemId))
            {
                SavedItemRow row = RowOfStored(stored);
                row.Placement = ItemPlacementKind.Container;
                row.ContainerId = stored.ParentContainerId;
                row.Slot = stored.Slot;
                items.Unowned.Add(row.ToJson(owned: false));
            }
        items.Unowned.AddRange(kept.Unowned.Select(row => row.DeepClone()));
        items.Authored.AddRange(destroyedAuthoredRecords.Select(row => row.DeepClone()));
        items.Authored.AddRange(kept.Authored.Select(row => row.DeepClone()));
    }

    private static Dictionary<Guid, (LiveItems.LiveItem, NetworkedItem)> LiveCopies()
    {
        Dictionary<Guid, (LiveItems.LiveItem, NetworkedItem)> live = new();
        foreach (LiveItems.LiveItem record in LiveItems.GetAll())
            if (record.PersistentItemId != Guid.Empty && record.Placement is not ItemPlacementKind.Destroyed and not ItemPlacementKind.Container &&
                NetworkedItem.TryGet(record.NetId, out NetworkedItem item) && item?.Item != null && !PlayerItems.IsDataOnly(record))
                live[record.PersistentItemId] = (record, item);
        return live;
    }

    // An owned item's one copy: a cold row (or a data tool's ammo row, or a Lost and Found entry's), else its object, else a container's row.
    private static SavedItemRow OwnedRow(Guid id, Dictionary<Guid, (LiveItems.LiveItem Record, NetworkedItem Item)> live,
        Dictionary<Guid, StoredItemRecord> stored)
    {
        if (ColdInventories.TryGet(id, out SavedItemRow cold) || PlayerItems.TryGetAmmoRow(id, out cold) ||
            LostAndFoundServer.TryGetDataRow(id, out cold))
            return cold;
        if (live.TryGetValue(id, out (LiveItems.LiveItem Record, NetworkedItem Item) copy))
            return RowOf(copy.Record, copy.Item);
        return stored.TryGetValue(id, out StoredItemRecord record) ? RowOfStored(record) : null;
    }

    /// <summary>The item's saved row: its record, and its state from the object.</summary>
    internal static SavedItemRow RowOf(LiveItems.LiveItem record, NetworkedItem item)
    {
        ItemSpatialStateData checkpoint = null;
        // Ammo in a magazine is where its tool is, not where it was loaded (containers.md).
        if (record.Placement != ItemPlacementKind.Magazine)
            NetworkedItemManager.Instance?.TryGetLatestSpatialState(record.NetId, out checkpoint);
        ItemWorldParentKind parentKind = checkpoint?.WorldParentKind ?? record.WorldParentKind;
        ItemPlacementKind placement = checkpoint == null ? record.Placement : parentKind switch
        {
            ItemWorldParentKind.TrainInterior => ItemPlacementKind.TrainInterior,
            ItemWorldParentKind.StaticParent => ItemPlacementKind.StaticParent,
            _ => ItemPlacementKind.World
        };
        return new SavedItemRow
        {
            PersistentItemId = record.PersistentItemId,
            AuthoredItemKey = record.AuthoredItemKey,
            PrefabName = record.PrefabName,
            DisplayName = item.Item?.InventorySpecs?.LocalizedName ?? string.Empty,
            Revision = record.Revision,
            HasPersistentOverride = record.HasPersistentOverride || checkpoint != null,
            IsReplenishableStockFork = record.IsReplenishableStockFork,
            Placement = placement,
            OwnerIdentity = record.Owner,
            PlacementPlayerIdentity = record.Holder,
            ClaimSlot = record.InventoryClaimSlot,
            Slot = record.Placement == ItemPlacementKind.PlayerInventory ? record.Slot
                : record.Placement == ItemPlacementKind.Magazine ? record.MagazineSlot : -1,
            ContainerId = record.Placement == ItemPlacementKind.Magazine ? record.MagazineToolId : Guid.Empty,
            ClaimFlags = record.InventoryClaimFlags,
            WorldParentKind = parentKind,
            WorldParentNetId = checkpoint?.WorldParentNetId ?? record.WorldParentNetId,
            WorldParentKey = checkpoint?.WorldParentKey ?? record.WorldParentKey ?? string.Empty,
            WorldParentPersistentId = record.WorldParentPersistentId,
            AttachedCarPersistentId = record.AttachedCarPersistentId,
            AttachedFront = record.AttachedFront,
            ParentLocalPosition = checkpoint?.ParentLocalPosition ?? record.ParentLocalPosition,
            ParentLocalRotation = checkpoint?.ParentLocalRotation ?? record.ParentLocalRotation,
            Position = checkpoint?.AbsolutePosition ?? record.Position,
            Rotation = checkpoint?.Rotation ?? record.Rotation,
            State = TrySaveItemState(record.NetId, item)
        };
    }

    internal static SavedItemRow RowOfStored(StoredItemRecord stored)
    {
        SavedItemRow row = new()
        {
            PersistentItemId = stored.PersistentItemId,
            AuthoredItemKey = stored.AuthoredItemKey ?? string.Empty,
            PrefabName = stored.PrefabName,
            DisplayName = stored.DisplayName ?? string.Empty,
            Placement = ItemPlacementKind.Container,
            StateVersion = stored.StateVersion
        };
        row.TakeDetachedState(stored.DetachedState);
        return row;
    }

    private static JObject TrySaveItemState(ushort netId, NetworkedItem item)
    {
        try
        {
            return JobPapers.Complete(item.gameObject, item.GetComponent<ItemSaveData>()?.SaveItemData());
        }
        catch (Exception ex)
        {
            Multiplayer.LogWarning($"Item state save failed for {netId}: {ex.Message}\r\n{ex.StackTrace}");
            return null;
        }
    }

    // A player's item lying about is saved; any other unowned one comes back from its source each
    // load (the paint station's spray can), so saving it added a copy every load. Whatever a player
    // carries is saved with them, as vanilla's inventory keeps everything in it.
    private static bool ShouldPersistUnowned(LiveItems.LiveItem record, NetworkedItem item)
    {
        if (record.PersistentItemId == Guid.Empty)
            return false;
        if (IsPlayerPossession(record))
            return true;
        if (IsJobSystemDocument(record.PrefabName))
            return false;
        // Ammo in a magazine is saved with its tool (containers.md, "Ammo in a magazine").
        if (record.Placement == ItemPlacementKind.Magazine)
            return LiveItems.TryGet(record.MagazineNetId, out LiveItems.LiveItem tool) && tool.Placement != ItemPlacementKind.Magazine &&
                   NetworkedItem.TryGet(tool.NetId, out NetworkedItem toolItem) && toolItem != null &&
                   (tool.PersistentItemId != Guid.Empty && ServerLedger.Ledger.IsLive(tool.PersistentItemId) ||
                    ShouldPersistUnowned(tool, toolItem));
        return item.Item?.InventorySpecs?.BelongsToPlayer == true &&
               record.Placement is ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
                   ItemPlacementKind.StaticParent or ItemPlacementKind.Attached or ItemPlacementKind.SnappedAttachment;
    }

    private static bool AuthoredMoved(LiveItems.LiveItem record) =>
        record.Placement is ItemPlacementKind.World or ItemPlacementKind.TrainInterior or ItemPlacementKind.StaticParent or
            ItemPlacementKind.Attached or ItemPlacementKind.SnappedAttachment &&
        (record.HasPersistentOverride || (record.Position - record.BaselinePosition).sqrMagnitude > 0.0001f ||
         Quaternion.Angle(record.Rotation, record.BaselineRotation) > 0.1f || record.Placement != record.BaselinePlacement);

    // A scene item in a player's inventory becomes an ordinary one before it is saved there, so the
    // scene doesn't make it again beside the saved copy (OI-PER-16).
    private static void PromoteSceneItemsInInventories()
    {
        foreach (LiveItems.LiveItem record in LiveItems.GetAll().ToArray())
        {
            if (!IsPlayerPossession(record) || !NetworkedItem.TryGet(record.NetId, out NetworkedItem item) ||
                item == null || !item.IsSceneAuthored)
                continue;
            RecordDestroyed(item);
            LiveItems.PromoteAuthoredToPersistent(item);
            item.DetachAuthoredItemKey();
        }
    }

    // A slot for an item saved from a hand: its claim, else the lowest free one, the belt last; -1
    // when the inventory is full.
    private static int SaveSlot(Guid id, OwnerRef owner, int claim, Guid holder, SaveContext context)
    {
        if (holder == Guid.Empty)
            return -1;
        bool Taken(int slot) => context.HandSlots.Contains((holder, slot)) ||
            ServerLedger.Ledger.CanTake(Home.InSlot(holder, slot), id, owner).Length > 0;
        int chosen = claim >= 0 && owner.IsPlayer(holder) && InventorySlots.IsValid(claim) && !Taken(claim)
            ? claim : InventorySlots.FirstFree(Taken);
        if (chosen >= 0)
            context.HandSlots.Add((holder, chosen));
        return chosen;
    }

    private static LedgerEntry WithHome(LedgerEntry entry, Home home) =>
        new(entry.Id, entry.Owner, entry.Prefab, home, entry.Born, entry.ClaimSlot, entry.ShopStocked, entry.Ended,
            entry.Essential);

    private static StorageItemData MirrorItem(SavedItemRow row, int slot, bool owned) =>
        new(row.PrefabName, Vector3.zero, Quaternion.identity, owned, state: row.State, inventorySlotIndex: slot);

    // Vanilla's lists (D2): the host's own player's inventory, so the career opens in single player
    // with their things; nothing in the world or Lost and Found lists. Never read back.
    private static void WriteVanillaMirror(SaveGameData data, SaveContext context)
    {
        if (data == null)
            return;
        // The host's own player's cold rows, when they aren't in the game (a listen host's player
        // is always here; a dedicated host has none).
        foreach (SavedItemRow row in ColdInventories.Of(context.MirrorHolder))
            if (context.Mirror.All(item => item.inventorySlotIndex != row.Slot))
                context.Mirror.Add(MirrorItem(row, row.Slot, ServerLedger.Ledger.IsLive(row.PersistentItemId)));
        data.SetObject(SaveGameKeys.Storage_Inventory, context.Mirror.OrderBy(item => item.inventorySlotIndex).ToList());
        data.SetObject(SaveGameKeys.Storage_World, new List<StorageItemData>());
        data.SetObject(SaveGameKeys.Storage_LostAndFound, new List<StorageItemData>());
    }
}
