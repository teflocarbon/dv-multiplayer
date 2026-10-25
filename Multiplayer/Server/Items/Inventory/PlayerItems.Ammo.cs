using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Protocol.Data.Items;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Server.Items.Inventory;

internal static partial class PlayerItems
{
    // A loaded magazine as data (docs/design/items/containers.md, "Ammo in a magazine"; OI-CON-16):
    // while a tool is data in a connected player's slot, its ammo is data too. Each keeps its record
    // (home Magazine(tool, slot)) and its NetId, reserved, with its row here and no object on the
    // server; the holder's stand-in has the same rows nested in its state (StandInAmmo). The tool's
    // object made again makes them with it, under those NetIds, as the holder's take-out predicted
    // them. The holder leaving makes them rows of the tool's container, as any loaded tool gone cold.

    private static readonly Dictionary<Guid, SavedItemRow> ammoRows = new();

    /// <summary>The rows of ammo kept as data inside tools kept as data.</summary>
    public static IEnumerable<SavedItemRow> AmmoRows() => ammoRows.Values;

    public static bool TryGetAmmoRow(Guid id, out SavedItemRow row) => ammoRows.TryGetValue(id, out row);

    /// <summary>A re-keyed owner's ammo rows follow them (OI-ID-19).</summary>
    public static int RekeyAmmo(Guid from, Guid to)
    {
        int moved = 0;
        foreach (SavedItemRow row in ammoRows.Values.Where(row => row.OwnerIdentity == from))
        {
            row.OwnerIdentity = to;
            moved++;
        }
        return moved;
    }

    private static bool IsDataOnlyAmmo(LiveItems.LiveItem record) =>
        record != null && record.Placement == ItemPlacementKind.Magazine && ammoRows.ContainsKey(record.PersistentItemId) &&
        !(NetworkedItem.TryGet(record.NetId, out NetworkedItem item) && item != null);

    private static LiveItems.LiveItem[] DataAmmoOf(ushort toolNetId) => LiveItems.AmmoIn(toolNetId).Where(IsDataOnlyAmmo).ToArray();

    /// <summary>
    /// Whether a loaded tool's ammo can go to data with it: each item in its magazine has a record
    /// in that magazine, with an id. Else the tool stays an object in its slot, as before OI-CON-16.
    /// </summary>
    internal static bool AmmoCanGoData(NetworkedItem tool) =>
        StandInAmmo.Loaded(tool.gameObject).All(loaded =>
            loaded.Ammo.TryGetComponent(out NetworkedItem ammo) && LiveItems.TryGet(ammo.NetId, out LiveItems.LiveItem record) &&
            record.Placement == ItemPlacementKind.Magazine && record.MagazineNetId == tool.NetId &&
            record.PersistentItemId != Guid.Empty);

    // What each holder's stand-in nested as they put a tool away, by the tool's NetId: checked against
    // the server's own magazine as the tool goes (or stays), and their stand-in corrected if they differ.
    private static readonly Dictionary<ushort, HashSet<ushort>> stashedNesting = new();

    // The stash's state, the ammo's split off: each item's goes over its own row as its tool's does.
    // Only this tool's ammo here counts: a NetId the server has anywhere else keeps its own state.
    private static string SplitStashedAmmo(ushort toolNetId, string state)
    {
        HashSet<ushort> nested = new();
        foreach (StandInAmmo.Entry entry in StandInAmmo.Of(state))
        {
            nested.Add(entry.NetId);
            if (entry.State != null && IsAmmoOf(entry.NetId, toolNetId))
                stashedStates[entry.NetId] = entry.State;
        }
        stashedNesting[toolNetId] = nested;
        return StandInAmmo.Without(state);
    }

    private static bool IsAmmoOf(ushort ammoNetId, ushort toolNetId) =>
        LiveItems.TryGet(ammoNetId, out LiveItems.LiveItem record) && record.Placement == ItemPlacementKind.Magazine &&
        record.MagazineNetId == toolNetId;

    // Whether the holder's stash nested other ammo than <paramref name="server"/>, the NetIds this
    // magazine holds on the server; false with no stash to go by. Forgets what they nested.
    private static bool NestingDiffers(ushort toolNetId, IEnumerable<ushort> server)
    {
        if (!stashedNesting.TryGetValue(toolNetId, out HashSet<ushort> theirs))
            return false;
        stashedNesting.Remove(toolNetId);
        return !theirs.SetEquals(server);
    }

    // A loaded tool the holder's game stood in stays an object here (AmmoCanGoData): their stand-in
    // is given the server's word, the tool's state with only the ammo this magazine has a record of,
    // so a take-out makes no ammo the server has elsewhere.
    private static void CorrectNesting(LiveItems.LiveItem record, NetworkedItem tool, ServerPlayer holder)
    {
        string captured = StandInAmmo.Capture(tool.gameObject);
        string state = StandInAmmo.With(StandInAmmo.Without(captured),
            StandInAmmo.Of(captured).Where(entry => IsAmmoOf(entry.NetId, tool.NetId)));
        Multiplayer.LogWarning($"Inventory: {holder.Username}'s stand-in of {record.PrefabName} (netId {record.NetId}) nested other ammo " +
                               "than the server's magazine; it is given the server's");
        NetworkServer.Instance.SendInventoryRows(holder, new List<ItemUpdate> { CreateOf(record) }, new List<string> { state });
    }

    // A row's state as its holder's stand-in keeps it: the item's own, with its tool's data ammo nested.
    private static string StandInState(ushort netId, JObject state)
    {
        string own = state?.ToString(Formatting.None) ?? string.Empty;
        return StandInAmmo.With(own, DataAmmoOf(netId).Where(ammo => ammoRows.ContainsKey(ammo.PersistentItemId))
            .Select(ammo => new StandInAmmo.Entry
            {
                Slot = ammo.MagazineSlot,
                NetId = ammo.NetId,
                PrefabName = ammo.PrefabName,
                Revision = ammo.Revision,
                State = ammoRows[ammo.PersistentItemId].State
            }));
    }

    // GoData: the tool goes to data, and its ammo with it. Whoever keeps the tool (its holder, an
    // owner's silhouette) keeps the ammo; anyone else who was shown it is told it's gone.
    private static void AmmoGoesData(NetworkedItem tool, string where, IReadOnlyCollection<byte> keeping)
    {
        StandInAmmo.RetireLoaded(tool.gameObject, ammo =>
        {
            if (!LiveItems.TryGet(ammo.NetId, out LiveItems.LiveItem record) || record.PersistentItemId == Guid.Empty)
            {
                Multiplayer.LogWarning($"Inventory: {ammo.name} (netId {ammo.NetId}) in {tool.name} has no record; it goes with its tool");
                Object.Destroy(ammo.gameObject);
                return;
            }
            SavedItemRow row = ItemSaveLoad.RowOf(record, ammo);
            if (stashedStates.TryGetValue(record.NetId, out JObject stashed))
                row.State = WithHolderValues(row.State, stashed);
            stashedStates.Remove(record.NetId);
            ammoRows[record.PersistentItemId] = row;
            foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
            {
                bool knew = player.KnownItems.Remove(ammo);
                player.NearbyItems.Remove(ammo);
                player.AcknowledgedWorldItems.Remove(ammo.NetId);
                if (!knew || keeping.Contains(player.PlayerId))
                    continue;
                ItemUpdate destroy = ammo.CreateUpdateData(ItemUpdate.ItemUpdateType.Destroy);
                if (destroy == null)
                    continue;
                LiveItems.WriteToSnapshot(record, destroy, ItemTransitionReason.WentCold);
                destroy.RemovalCause = ItemChangeLog.Removal(ItemRemovalReason.WentCold,
                    detail: $"in {tool.name}, in {where}, kept as data on the server");
                NetworkServer.Instance.SendItemUpdate(destroy, player);
            }
            knownBy[record.NetId] = keeping.ToArray();
            ServerItemManager.Instance?.UnregisterHostWorldItem(ammo);
            ammo.HandOverToData();
            StorageIntegration.MoveTo(ammo.Item, StorageMembership.None);
            Object.Destroy(ammo.gameObject);
        });
    }

    // TakeUpObject: the tool's object is made again, and its ammo with it, under its NetIds, loaded
    // in its magazine (the correction snapshot's InMagazine). The holder made theirs as they took it out.
    private static void TakeUpAmmo(LiveItems.LiveItem tool)
    {
        foreach (LiveItems.LiveItem ammo in DataAmmoOf(tool.NetId))
        {
            SavedItemRow row = ammoRows[ammo.PersistentItemId];
            GameObject instance = InstantiateFromRow(ammo.NetId, row, out NetworkedItem item);
            if (instance == null)
                continue;
            try
            {
                TakeUpObject(ammo, row, item, null);
                ammoRows.Remove(ammo.PersistentItemId);
            }
            catch (Exception exception)
            {
                item.AbortColdMaterialization();
                Object.Destroy(instance);
                Multiplayer.LogWarning($"Inventory: couldn't make {row.PrefabName} (netId {ammo.NetId}) in its tool from its data: {exception.Message}");
            }
            finally
            {
                materializing.Remove(ammo.NetId);
            }
        }
    }

    // DataGoesCold: the tool's holder left, and its row stays cold. Its ammo becomes rows of its
    // container, Container(tool, slot), as a loaded tool's object gone cold does (MagazineServer).
    private static void AmmoGoesCold(LiveItems.LiveItem tool, ServerPlayer leaver)
    {
        LiveItems.LiveItem[] ammo = DataAmmoOf(tool.NetId);
        if (ammo.Length == 0)
            return;
        Guid toolId = tool.PersistentItemId;
        bool contained = toolId != Guid.Empty && ItemSaveLoad.EnsureToolContainer(toolId, tool.PrefabName);
        foreach (LiveItems.LiveItem record in ammo)
        {
            SavedItemRow row = ammoRows[record.PersistentItemId];
            ammoRows.Remove(record.PersistentItemId);
            Guid id = record.PersistentItemId;
            int slot = record.MagazineSlot;
            bool owned = ServerLedger.Ledger.IsLive(id);
            Home loaded = Home.Magazine(toolId, slot), stowed = Home.Container(toolId, slot);
            string refused = contained ? string.Empty : "its tool has no container";
            if (refused.Length == 0 && owned)
                refused = ServerLedger.CommitStored(id, record.PrefabName, loaded, stowed,
                    ItemChangeLog.Move(ItemMoveReason.DepositedInContainer, leaver, $"with its tool {tool.PrefabName}, gone cold"));
            if (refused.Length == 0 && !ContainerServer.Graph.Stow(ItemSaveLoad.StoredOf(row, toolId, slot), out refused) && owned)
                ServerLedger.CommitStored(id, record.PrefabName, stowed, loaded,
                    ItemChangeLog.Move(ItemMoveReason.HostCorrection, detail: "not stowed with its tool: " + refused));
            if (refused.Length > 0)
                Multiplayer.LogError($"Inventory: {record.PrefabName} ({id:D}) in {tool.PrefabName} couldn't go cold with it: {refused}; it is left where its holder was");

            ItemUpdate destroy = new()
            {
                UpdateType = ItemUpdate.ItemUpdateType.Destroy,
                ItemNetId = record.NetId,
                PrefabName = record.PrefabName
            };
            LiveItems.WriteToSnapshot(record, destroy, ItemTransitionReason.WentCold);
            destroy.RemovalCause = ItemChangeLog.Removal(ItemRemovalReason.WentCold, leaver, $"in {tool.PrefabName}, kept as data");
            if (knownBy.TryGetValue(record.NetId, out byte[] knowers))
                foreach (byte playerId in knowers)
                    if (playerId != leaver?.PlayerId && NetworkServer.Instance.TryGetServerPlayer(playerId, out ServerPlayer knower))
                        NetworkServer.Instance.SendItemUpdate(destroy, knower);
            knownBy.Remove(record.NetId);
            LiveItems.Remove(record.NetId);
            NetworkedItem.FreeDataNetId(record.NetId);
            if (refused.Length > 0)
                AmmoLeftBehind(row, id, record.PrefabName, loaded, leaver, refused);
        }
    }

    // Ammo that couldn't go cold with its tool isn't lost: its object is made where its holder was,
    // and an owned item goes on to its owner's Lost and Found, as a destroyed tool's ammo does
    // (MagazineServer.Release).
    private static void AmmoLeftBehind(SavedItemRow row, Guid id, string prefabName, Home loaded, ServerPlayer leaver, string why)
    {
        bool owned = ServerLedger.Ledger.IsLive(id);
        row.Placement = ItemPlacementKind.World;
        row.WorldParentKind = ItemWorldParentKind.World;
        row.Position = (leaver != null ? leaver.AbsoluteWorldPosition : ContainerServer.SafetyPosition) + Vector3.up * 0.3f;
        row.Rotation = Quaternion.identity;
        row.ContainerId = Guid.Empty;
        row.Slot = -1;
        // From wherever the ledger has it: a stow's rollback that was refused left it in the tool's
        // container. A refusal here still leaves the object at the holder's feet, where the home
        // check finds it, rather than nowhere.
        Home from = owned && ServerLedger.Ledger.TryGetHome(id, out Home had) ? had : loaded;
        string refused = owned
            ? ServerLedger.CommitStored(id, prefabName, from, Home.World,
                ItemChangeLog.Move(ItemMoveReason.UnloadedFromMagazine, leaver, "its tool couldn't keep it: " + why))
            : string.Empty;
        if (refused.Length > 0)
            Multiplayer.LogError($"Inventory: {prefabName} ({id:D}): the ledger refused to move it to where its holder was ({refused}); " +
                                 "it is made there anyway");
        if (!ItemSaveLoad.MakeLive(row, owned, out LiveItems.LiveItem made) ||
            !NetworkedItem.TryGet(made.NetId, out NetworkedItem item) || item == null)
        {
            Multiplayer.LogError($"Inventory: {prefabName} ({id:D}) couldn't be made where its holder was");
            return;
        }
        if (owned && refused.Length == 0)
            LostAndFoundServer.Collect(item, LostItemReason.ContainerCleared, "its tool went cold without it");
    }

    // TakeUp: a tool's row goes live as data (a join), and the ammo rows of its container go with it,
    // data in its magazine under reserved NetIds, for its holder's stand-in to nest. A box's rows
    // aren't ammo: only a tool with a magazine has any (MagazineServer.LoadFromRows).
    private static void AmmoFromRows(LiveItems.LiveItem tool, ServerPlayer holder)
    {
        Guid toolId = tool.PersistentItemId;
        ContainerGraph graph = ContainerServer.Graph;
        if (toolId == Guid.Empty || !graph.IsContainer(toolId) ||
            ItemSaveLoad.FindSpec(tool.PrefabName)?.GetComponentInChildren<DV.Items.ItemMagazine>(true) == null)
            return;
        foreach (StoredItemRecord stored in graph.RowsIn(toolId))
        {
            Guid id = stored.PersistentItemId;
            if (!graph.IsSettled(id) || !InventoryStandIn.CanStandIn(stored.PrefabName))
                continue;
            Guid.TryParse(graph.OwnerOf(id), out Guid owner);
            Home stowed = Home.Container(toolId, stored.Slot), loaded = Home.Magazine(toolId, stored.Slot);
            if (owner != Guid.Empty && ServerLedger.CommitStored(id, stored.PrefabName, stowed, loaded,
                    ItemChangeLog.Move(ItemMoveReason.LoadedIntoMagazine, holder, $"its tool {tool.PrefabName} went live as a row"))
                    is { Length: > 0 } refused)
            {
                Multiplayer.LogWarning($"Inventory: {stored.PrefabName} ({id:D}) stays a row of {tool.PrefabName}: the ledger refused, {refused}");
                continue;
            }
            SavedItemRow row = ItemSaveLoad.RowOfStored(stored);
            row.OwnerIdentity = owner;
            row.Placement = ItemPlacementKind.Magazine;
            row.ContainerId = toolId;
            row.Slot = stored.Slot;
            ushort netId = NetworkedItem.ReserveDataNetId();
            LiveItems.LiveItem record = null;
            if (netId != 0 && graph.TakeOut(id, out _) != null)
                record = LiveItems.RegisterDataOnlyAmmo(netId, tool, row, stored.Slot);
            if (record == null)
            {
                Multiplayer.LogWarning($"Inventory: {stored.PrefabName} ({id:D}) stays a row of {tool.PrefabName}");
                if (netId != 0)
                    NetworkedItem.FreeDataNetId(netId);
                if (!graph.TryGetItem(id, out _))
                    graph.Stow(stored, out _);
                if (owner != Guid.Empty)
                    ServerLedger.CommitStored(id, stored.PrefabName, loaded, stowed,
                        ItemChangeLog.Move(ItemMoveReason.HostCorrection, detail: "a row of its tool again"));
                continue;
            }
            ammoRows[id] = row;
            knownBy[netId] = new[] { holder.PlayerId };
        }
    }
}
