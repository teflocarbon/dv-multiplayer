using DV.CabControls;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Core.Items.SaveLoad;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Debugging.Protocol;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items.SaveLoad;

/// <summary>
/// The load (docs/design/items/save-load.md, "Loading"): once, as soon as vanilla has loaded the
/// world and its cars, before any player's request is served. The ledger first; then each owned
/// item in its home (or as data, in a cold home), from its one saved state; then the unowned items,
/// the containers' rows and the scene's overrides. A row that won't load is kept as it was.
/// </summary>
internal static partial class ItemSaveLoad
{
    private static bool CanLoad() => StartingItemsController.Instance != null &&
        StartingItemsController.Instance.itemsLoaded && SaveGameManager.Instance?.data != null &&
        StorageController.Instance?.StorageLostAndFound != null && NetworkServer.Instance != null;

    private static void TryLoad()
    {
        if (loaded || !CanLoad())
            return;
        loaded = true;
        JObject root = SaveGameManager.Instance.data.GetJObject("Multiplayer");
        ItemsSaveCodec.ReadResult read = ItemsSaveCodec.Read(root?[ItemsSaveCodec.Key], out ItemsSave items);
        if (read == ItemsSaveCodec.ReadResult.Newer)
        {
            newerKey = root[ItemsSaveCodec.Key].DeepClone();
            Multiplayer.LogError("Items: this career's items were saved by a newer build of the mod; none are loaded, " +
                                 "and they are written back unchanged at each save");
            items = new ItemsSave();
        }
        else if (read == ItemsSaveCodec.ReadResult.Absent)
        {
            string[] old = ItemsSaveCodec.OldKeys.Where(key => root?[key] != null).ToArray();
            // Once, at the load (D1): the old keys are dropped at the next save.
            if (old.Length > 0)
                Multiplayer.LogWarning($"Items: this career was saved before Multiplayer.Items ({string.Join(", ", old)}); " +
                                       "its items aren't read, and every player's items start fresh");
        }
        try
        {
            Load(items);
        }
        catch (Exception exception)
        {
            Multiplayer.LogException("Items: the load failed part way", exception);
        }
        finally
        {
            ServerLedger.FinishLoad();
        }
        // The host's own player is admitted in-process before the load, so its rows go live now.
        foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers.ToArray())
            PlayerItems.Join(player);
    }

    private static void Load(ItemsSave items)
    {
        kept.Ledger.AddRange(items.UnreadableLedger);
        foreach (KeyValuePair<string, JToken> state in items.UnreadableStates)
            kept.States[state.Key] = state.Value;
        kept.LostAndFound.AddRange(items.UnreadableLostAndFound);
        if (items.UnreadableCount > 0)
            Multiplayer.LogError($"Items: {items.UnreadableCount} saved row(s) can't be read; they are kept as they are");

        // Job paperwork is the job system's to make (an R4 save before this wrote a claimed booklet),
        // except what a player carries, which is theirs (OI-COLD-15).
        items.Ledger.RemoveAll(IsUnsavedJobPaper);
        // An item whose prefab is gone holds no place from the start (D9): its saved row names the
        // slot it had, which an item made since may have taken, and the made item keeps it.
        for (int i = 0; i < items.Ledger.Count; i++)
        {
            LedgerEntry entry = items.Ledger[i];
            if (entry.IsLive && entry.Home.Kind != HomeKind.Installed && !PrefabKnown(entry.Prefab))
                items.Ledger[i] = WithHome(entry, Home.Unloadable);
        }
        ServerLedger.LoadEntries(items.Ledger, items.StarterKits);
        HashSet<string> cars = new(Resources.FindObjectsOfTypeAll<TrainCar>()
            .Where(car => car != null && !string.IsNullOrEmpty(car.CarGUID)).Select(car => car.CarGUID));
        LoadWorld world = new()
        {
            PrefabExists = PrefabKnown,
            CarExists = car => !string.IsNullOrEmpty(car) && cars.Contains(car)
        };

        // Containers first, so an unowned stored item knows its container's owner.
        Dictionary<Guid, ContainerRecord> containers = new();
        Dictionary<Guid, JToken> containerRows = new();
        foreach (JToken token in items.Containers)
        {
            ContainerRecord container = token is JObject row ? ItemsSaveCodec.ReadContainer(row) : null;
            if (container == null || containers.ContainsKey(container.PersistentContainerId))
            {
                kept.Containers.Add(token);
                continue;
            }
            containers[container.PersistentContainerId] = container;
            containerRows[container.PersistentContainerId] = token;
        }

        LoadContext context = new(items, containers);
        int made = 0, cold = 0;
        foreach (LoadStep step in ItemLoadPlanner.Plan(ServerLedger.Ledger.All(), new HashSet<Guid>(items.States.Keys), world))
        {
            try
            {
                switch (LoadOwned(step, context))
                {
                    case LoadAction.KeepCold: cold++; break;
                    case LoadAction.MakeLive or LoadAction.IntoLostAndFound or LoadAction.Recover: made++; break;
                }
            }
            catch (Exception exception)
            {
                Multiplayer.LogException($"Items: loading {step} failed; its rows are kept as they are", exception);
                KeepUnloadable(step.Id, items);
            }
        }
        foreach (JToken token in items.Unowned)
        {
            try
            {
                if (!LoadUnowned(token, context))
                    kept.Unowned.Add(token);
            }
            catch (Exception exception)
            {
                Multiplayer.LogWarning($"Items: an unowned item's row won't load ({exception.Message}); it is kept as it is");
                kept.Unowned.Add(token);
            }
        }

        foreach (ContainerRowRefusal refusal in ContainerServer.LoadRows(containers.Values, context.Stored))
            Refused(refusal, context, containerRows);

        foreach (JToken token in items.Authored)
        {
            try
            {
                if (!LoadAuthored(token))
                    kept.Authored.Add(token);
            }
            catch (Exception exception)
            {
                Multiplayer.LogWarning($"Items: a scene item's row won't load ({exception.Message}); it is kept as it is");
                kept.Authored.Add(token);
            }
        }

        Multiplayer.Log($"Items: loaded {made} item(s) into the world and Lost and Found, {cold} into offline " +
                        $"inventories, {context.Stored.Count} into containers; {keptOwned.Count} unloadable, " +
                        $"{kept.All().Count()} row(s) kept as they were");
        DebugRuntime.Publish("item-world", "items.loaded", DebugRuntimeSide.Server, data: new Dictionary<string, object>
        {
            ["made"] = made,
            ["cold"] = cold,
            ["stored"] = context.Stored.Count,
            ["unloadable"] = keptOwned.Count,
            ["kept"] = kept.All().Count()
        });
    }

    private sealed class LoadContext
    {
        public LoadContext(ItemsSave items, Dictionary<Guid, ContainerRecord> containers)
        {
            Items = items;
            Containers = containers;
        }

        public ItemsSave Items { get; }
        public Dictionary<Guid, ContainerRecord> Containers { get; }
        public List<StoredItemRecord> Stored { get; } = new();
        /// <summary>Stored items' rows by id: an owned one's state, to make it if its container refuses it.</summary>
        public Dictionary<Guid, SavedItemRow> StoredRows { get; } = new();
        /// <summary>Unowned stored items' rows as saved, kept if their container refuses them.</summary>
        public Dictionary<Guid, JToken> UnownedStoredTokens { get; } = new();
    }

    private static LoadAction LoadOwned(LoadStep step, LoadContext context)
    {
        ItemsSave items = context.Items;
        if (!ServerLedger.Ledger.TryGet(step.Id, out LedgerEntry entry))
            return LoadAction.Unloadable;
        Guid owner = entry.Owner.PlayerId;
        SavedItemRow row = null;
        string unreadable = null;
        if (items.States.TryGetValue(step.Id, out JObject state))
        {
            try
            {
                row = SavedItemRow.FromJson(state);
            }
            catch (Exception exception)
            {
                unreadable = exception.Message;
            }
        }

        switch (step.Action)
        {
            case LoadAction.LeaveInstalled:
                return step.Action;
            case LoadAction.Unloadable:
                Multiplayer.LogWarning($"Items: {entry.Prefab} ({entry.Id:D}), {ItemChangeLog.PlayerName(owner)}'s, " +
                                       $"can't be made ({step.Detail}); it is kept and tried again at the next load");
                KeepUnloadable(step.Id, items);
                return step.Action;
            case LoadAction.IntoLostAndFound:
                items.LostAndFound.TryGetValue(step.Id, out JObject lost);
                if (row == null)
                    Multiplayer.LogError($"Items: {entry.Prefab} ({entry.Id:D}) in Lost and Found has " +
                                         (unreadable != null ? $"a saved state that can't be read ({unreadable})" : "no saved state") +
                                         "; it is made fresh");
                MakeInLostAndFound(entry.Id, entry.Prefab, row, owner,
                    lost != null && Enum.TryParse((string)lost["reason"], out LostItemReason reason) ? reason : LostItemReason.SaveRecovery,
                    (long?)lost?["utc"] ?? DateTime.UtcNow.Ticks, (string)lost?["display"],
                    (ItemInventoryClaimFlags)((byte?)lost?["claimFlags"] ?? 0));
                return step.Action;
        }

        if (row == null)
            return Recover(entry, null, step.Action == LoadAction.Recover ? step.Reason : LostItemReason.SaveRecovery,
                unreadable != null ? $"its saved state can't be read ({unreadable})" : step.Detail);

        switch (step.Action)
        {
            case LoadAction.KeepCold:
                row.OwnerIdentity = owner;
                row.PlacementPlayerIdentity = step.Home.Holder;
                row.Slot = step.Home.Kind == HomeKind.Slot ? step.Home.Slot : -1;
                row.ClaimSlot = entry.ClaimSlot;
                return ColdInventories.Add(row) ? step.Action
                    : Recover(entry, row, LostItemReason.SaveRecovery, "its offline inventory refused it");
            case LoadAction.IntoContainer:
                context.Stored.Add(StoredOf(row, owner, Guid.Parse(step.Home.Anchor), step.Home.Slot));
                context.StoredRows[row.PersistentItemId] = row;
                return step.Action;
            case LoadAction.MakeLive:
                PlaceFromHome(row, step.Home);
                return MakeLive(row, owned: true) ? step.Action
                    : Recover(entry, null, LostItemReason.SaveRecovery, "it couldn't be made in its home " + step.Home);
            default:
                return Recover(entry, row, step.Reason, step.Detail);
        }
    }

    // Into its owner's Lost and Found, from its row, or fresh without one (D9). A gadget whose car is
    // gone is vanilla's object, which vanilla made and put in its Lost and Found list.
    private static LoadAction Recover(LedgerEntry entry, SavedItemRow row, LostItemReason reason, string detail)
    {
        bool fresh = row == null && reason != LostItemReason.GadgetDestinationMissing;
        string message = $"Items: {entry.Prefab} ({entry.Id:D}), {ItemChangeLog.PlayerName(entry.Owner.PlayerId)}'s, " +
                         $"goes to their Lost and Found ({reason}): {detail}{(fresh ? "; it is made fresh" : string.Empty)}";
        if (fresh)
            Multiplayer.LogError(message);
        else
            Multiplayer.LogWarning(message);
        MakeInLostAndFound(entry.Id, entry.Prefab, row, entry.Owner.PlayerId, reason, DateTime.UtcNow.Ticks,
            row?.DisplayName, ItemInventoryClaimFlags.None);
        return LoadAction.Recover;
    }

    // Its prefab is gone (or it couldn't load at all): its rows are written back as they were, and
    // the ledger holds it in no place, so its slot is free meanwhile.
    private static void KeepUnloadable(Guid id, ItemsSave items)
    {
        items.LedgerRows.TryGetValue(id, out JObject ledgerRow);
        items.States.TryGetValue(id, out JObject state);
        items.LostAndFound.TryGetValue(id, out JObject lost);
        if (ledgerRow == null)
            return;
        keptOwned[id] = new KeptItem { Ledger = ledgerRow, State = state, LostAndFound = lost };
        ServerLedger.Ledger.Repair(id, Home.Unloadable,
            new LedgerCause(ItemMoveReason.RestoredFromSave, Guid.Empty, "it can't be loaded"));
    }

    // An owned row has no home of its own: its ledger home says where it goes.
    private static void PlaceFromHome(SavedItemRow row, Home home)
    {
        switch (home.Kind)
        {
            case HomeKind.TrainInterior:
                row.Placement = ItemPlacementKind.TrainInterior;
                row.WorldParentKind = ItemWorldParentKind.TrainInterior;
                row.WorldParentPersistentId = home.Anchor;
                break;
            case HomeKind.Shelf:
                row.Placement = ItemPlacementKind.StaticParent;
                row.WorldParentKind = ItemWorldParentKind.StaticParent;
                row.WorldParentKey = home.Anchor;
                break;
            case HomeKind.Snapped:
                row.Placement = ItemPlacementKind.SnappedAttachment;
                row.WorldParentKey = home.Anchor;
                break;
            case HomeKind.Attached:
                row.Placement = ItemPlacementKind.Attached;
                row.AttachedCarPersistentId = home.Anchor;
                row.AttachedFront = home.Front;
                break;
            default:
                row.Placement = ItemPlacementKind.World;
                row.WorldParentKind = ItemWorldParentKind.World;
                break;
        }
    }

    private static StoredItemRecord StoredOf(SavedItemRow row, Guid owner, Guid container, int slot) => new()
    {
        PersistentItemId = row.PersistentItemId,
        PrefabName = row.PrefabName,
        DisplayName = row.DisplayName,
        PersistentOwnerIdentity = owner == Guid.Empty ? string.Empty : owner.ToString("D"),
        AuthoredItemKey = row.AuthoredItemKey,
        ParentContainerId = container,
        Slot = slot,
        ChildContainerId = row.ChildContainerId == Guid.Empty ? null : row.ChildContainerId,
        StateVersion = row.StateVersion,
        DetachedState = row.DetachedState()
    };

    private static bool LoadUnowned(JToken token, LoadContext context)
    {
        SavedItemRow row = SavedItemRow.FromJson((JObject)token);
        // Saves from earlier builds have job paperwork the job system makes by itself; what a player
        // carries is theirs (OI-COLD-15), and is made from its job, not from a prefab.
        if (IsUnsavedJobPaper(row.PrefabName, row.Placement))
            return true;
        if (FindSpec(row.PrefabName) == null && !JobPapers.IsPaper(row.PrefabName))
        {
            Multiplayer.LogWarning($"Items: no item called {row.PrefabName}; its row is kept and tried again at the next load");
            return false;
        }
        switch (row.Placement)
        {
            case ItemPlacementKind.PlayerInventory:
            case ItemPlacementKind.PlayerHand:
                // An unowned item a player carried comes back with them (OI-PER-19).
                if (row.Placement == ItemPlacementKind.PlayerHand)
                    row.Slot = -1;
                row.OwnerIdentity = Guid.Empty;
                return ColdInventories.Add(row);
            case ItemPlacementKind.Container:
                // An unowned stored item is its container's owner's to see; the ledger doesn't hold it.
                if (!context.Containers.TryGetValue(row.ContainerId, out ContainerRecord container))
                    return false;
                Guid.TryParse(container.OwnerIdentity, out Guid owner);
                context.Stored.Add(StoredOf(row, owner, row.ContainerId, row.Slot));
                context.StoredRows[row.PersistentItemId] = row;
                context.UnownedStoredTokens[row.PersistentItemId] = token;
                return true;
            default:
                return MakeLive(row, owned: false);
        }
    }

    // A container or stored item the graph refused stands alone (OI-CON-04): a container's row is
    // kept; an owned item goes to its owner's Lost and Found; an unowned one's row is kept.
    private static void Refused(ContainerRowRefusal refusal, LoadContext context, Dictionary<Guid, JToken> containerRows)
    {
        if (refusal.Item == null)
        {
            Multiplayer.LogError($"Items: container {refusal.Container?.PersistentContainerId:D} won't load ({refusal.Reason}); its row is kept");
            if (refusal.Container != null && containerRows.TryGetValue(refusal.Container.PersistentContainerId, out JToken row))
                kept.Containers.Add(row);
            return;
        }
        Guid id = refusal.Item.PersistentItemId;
        if (context.UnownedStoredTokens.TryGetValue(id, out JToken token))
        {
            Multiplayer.LogError($"Items: {refusal.Item.PrefabName} ({id:D}) won't go into its container ({refusal.Reason}); its row is kept");
            kept.Unowned.Add(token);
            return;
        }
        if (ServerLedger.Ledger.TryGet(id, out LedgerEntry entry) && entry.IsLive)
        {
            context.StoredRows.TryGetValue(id, out SavedItemRow row);
            Recover(entry, row, LostItemReason.SaveRecovery, $"its container refused it ({refusal.Reason})");
        }
    }

    private static bool LoadAuthored(JToken token)
    {
        SavedItemRow row = SavedItemRow.FromJson((JObject)token);
        if (row.IsTombstone)
            return RestoreAuthoredTombstone(row, (JObject)token);
        if (!WorldItemStableIdentity.IsValid(row.AuthoredItemKey))
            return false;
        return MakeLive(row, owned: false);
    }
}
