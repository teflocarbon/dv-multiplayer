using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Inventory;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Jobs;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Newtonsoft.Json;

namespace Multiplayer.Server.Items.Inventory;

internal static partial class PlayerItems
{
    // The join as rows (docs/design/items/inventory.md, "Joining"; OI-COLD-03, OI-JOIN-02; D14's C1
    // server half). A remote joiner's inventory is read from the ledger: each entry in their slots,
    // with its cold row, and the unowned rows they carry. Each becomes a live record kept as data
    // only, under a NetId reserved for it, in its exact slot; the joiner is sent the rows with their
    // state, and their game makes the only objects. No object is made on the server.

    /// <summary>
    /// Host: a remote player's game is ready for items. Their cold inventory goes live as rows, and
    /// they are sent any essential of theirs another player holds as data (OI-ESS-17).
    /// </summary>
    public static void SendRows(ServerPlayer player)
    {
        if (player?.Peer == null || player.Guid == Guid.Empty || player == NetworkServer.Instance.LocalPlayer ||
            NetworkLifecycle.Instance?.IsHost() != true)
            return;
        if (!ServerLedger.Loaded)
        {
            // The load runs before anyone can be ready for items; if it hasn't, the rows go as objects.
            foreach (SavedItemRow row in ColdInventories.Of(player.Guid))
                CoroutineManager.Instance.StartCoroutine(GoLive(player, row));
            return;
        }

        List<ItemUpdate> rows = new();
        List<string> states = new();
        int taken = 0;
        foreach (SavedItemRow row in RowsOf(player))
        {
            ItemUpdate create = TakeUp(player, row);
            if (create == null)
                continue;
            rows.Add(create);
            states.Add(StandInState(create.ItemNetId, row.State));
            taken++;
        }
        NetworkServer.Instance.SendInventoryRows(player, rows, states, full: true);
        if (taken > 0)
            Multiplayer.Log($"Inventory: {player.Username} joined; sent {taken} item(s) of their inventory as rows");

        SendHeldEssentials(player);
    }

    // What the ledger has in their slots (OI-JOIN-02), each with its cold row, then the rest of their
    // rows: unowned items they carry, which the ledger doesn't hold, and one the ledger moved to Lost
    // and Found (a re-key merge), which TakeUp sends there.
    private static IEnumerable<SavedItemRow> RowsOf(ServerPlayer player)
    {
        HashSet<Guid> seen = new();
        foreach (LedgerEntry entry in ServerLedger.Ledger.LiveWith(player.Guid))
            if (entry.Home.Kind == HomeKind.Slot && ColdInventories.TryGet(entry.Id, out SavedItemRow row) && seen.Add(entry.Id))
                yield return row;
        foreach (SavedItemRow row in ColdInventories.Of(player.Guid))
            if (seen.Add(row.PersistentItemId))
                yield return row;
    }

    // One row goes live as data: a record in its slot under a reserved NetId, its row kept. An item
    // the ledger moved to Lost and Found, one with no free slot, and one that can't be a stand-in
    // (InventoryStandIn.CanStandIn) are made as objects instead (GoLive), as before. A job paper
    // joins its job, or goes if its job is over (OI-COLD-07). Returns its Create, or null.
    private static ItemUpdate TakeUp(ServerPlayer player, SavedItemRow row)
    {
        bool lost = ServerLedger.Ledger.TryGetHome(row.PersistentItemId, out Home home) && home.Kind == HomeKind.LostAndFound;
        int slot = lost ? -1
            : ServerLedger.FreeSlot(row.PersistentItemId, row.OwnerIdentity, row.ClaimSlot, player.Guid, row.Slot, row.ClaimSlot);
        // A job paper is one while its job wants it (OI-COLD-07): a booklet of a job over since the
        // save goes, as vanilla's load destroys the booklets no job claims.
        NetworkedJob job = null;
        bool paper = JobPapers.IsPaper(row.PrefabName);
        // A report has no job to print it again. One saved before C7 has no recipe either: it can never
        // be made (nor shown), so it goes rather than keep a slot for nothing.
        bool report = row.PrefabName == nameof(JobReport);
        if (report && !JobPapers.HasRecipe(row.State))
        {
            JobPaperRows.Drop(row, "it was saved before C7, with no recipe to make it from");
            return null;
        }
        if (paper && !JobPaperRows.Wanted(row, out job))
        {
            JobPaperRows.Drop(row, "its job is over or has another");
            return null;
        }
        // A booklet or overview with no slot goes (its job prints another); a report goes to Lost and
        // Found, as any item with no slot does (GoLive).
        if (paper && !report && slot < 0)
        {
            JobPaperRows.Drop(row, "no slot for it");
            return null;
        }
        if (slot < 0 || !ItemSaveLoad.PrefabKnown(row.PrefabName) || !InventoryStandIn.CanStandIn(row.PrefabName))
        {
            CoroutineManager.Instance.StartCoroutine(GoLive(player, row));
            return null;
        }
        ushort netId = NetworkedItem.ReserveDataNetId();
        if (netId == 0)
        {
            Multiplayer.LogError($"Inventory: no NetId left for {player.Username}'s {row.PrefabName}; it stays cold");
            return null;
        }
        ChangeCause<ItemMoveReason> cause = ItemChangeLog.Move(ItemMoveReason.WentLive, player, "slot " + slot + ", as a row");
        LiveItems.LiveItem record = LiveItems.RegisterDataOnly(netId, player, row, slot, cause);
        if (record == null)
        {
            NetworkedItem.FreeDataNetId(netId);
            return null;
        }
        if (record.Slot != row.Slot && row.Slot >= 0)
            Multiplayer.LogWarning($"Inventory: {player.Username}'s {row.PrefabName} came back in slot {record.Slot}, not its slot {row.Slot}");
        row.Slot = record.Slot;
        row.PlacementPlayerIdentity = player.Guid;
        knownBy[netId] = new[] { player.PlayerId };
        AmmoFromRows(record, player);
        if (paper)
            JobPaperRows.Admit(record, row.PrefabName, player, job);
        ItemUpdate create = CreateOf(record);
        create.MoveCause = cause;
        return create;
    }

    // OI-ESS-17: an essential of the joiner's that another connected player holds as data only has
    // no object for interest to find, so the joiner is sent it here, and kept up to date with it.
    private static void SendHeldEssentials(ServerPlayer owner)
    {
        foreach (LiveItems.LiveItem record in LiveItems.Records.ToArray())
        {
            if (!JoinRows.SendsHeldEssential(record.Owner, record.Holder, record.InventoryClaimSlot, owner.Guid) ||
                !IsDataOnly(record))
                continue;
            NetworkServer.Instance.SendItemUpdate(CreateOf(record), owner);
            byte[] knowers = knownBy.TryGetValue(record.NetId, out byte[] known) ? known : Array.Empty<byte>();
            if (!knowers.Contains(owner.PlayerId))
                knownBy[record.NetId] = knowers.Append(owner.PlayerId).ToArray();
        }
    }

    /// <summary>
    /// An item the server puts straight into a player's inventory (a starting item, a grant, a
    /// withdrawal; D14, OI-COLD-11): a row kept as data from the start, in <paramref name="slot"/>,
    /// under a reserved NetId, with no object made on the server. A remote player is sent the row
    /// with its state and draws its stand-in; the host's own player's is drawn here. The ledger
    /// settles it where it now is (born there if it's new), or <paramref name="own"/> makes it its
    /// owner's. Its record, or null if it couldn't be made (nothing changed).
    /// </summary>
    internal static LiveItems.LiveItem Issue(ServerPlayer player, SavedItemRow row, int slot,
        ChangeCause<ItemMoveReason> cause, Func<LiveItems.LiveItem, string> own = null)
    {
        if (player == null || player.PlayerId == 0 || row == null || row.PersistentItemId == Guid.Empty || slot < 0)
            return null;
        row.PlacementPlayerIdentity = player.Guid;
        row.Slot = slot;
        if (!ColdInventories.Add(row))
        {
            Multiplayer.LogWarning($"Inventory: {row.PrefabName} ({row.PersistentItemId:D}) is already a row; not made again for {player.Username}");
            return null;
        }
        ushort netId = NetworkedItem.ReserveDataNetId();
        LiveItems.LiveItem record = netId == 0 ? null : LiveItems.RegisterDataOnly(netId, player, row, slot, cause, own);
        if (record == null)
        {
            if (netId != 0)
                NetworkedItem.FreeDataNetId(netId);
            ColdInventories.Remove(row.PersistentItemId);
            Multiplayer.LogError($"Inventory: couldn't make {player.Username}'s {row.PrefabName} as a row in slot {slot}" +
                                 (netId == 0 ? ": no NetId left" : string.Empty));
            return null;
        }
        knownBy[netId] = new[] { player.PlayerId };
        ItemChangeLog.RecordMove(null, record, cause);
        ItemUpdate create = CreateOf(record);
        create.MoveCause = cause;
        string state = row.State?.ToString(Formatting.None) ?? string.Empty;
        if (player == NetworkServer.Instance.LocalPlayer)
            HostInventory.Draw(create, state);
        else
            NetworkServer.Instance.SendInventoryRows(player, new List<ItemUpdate> { create }, new List<string> { state });
        return record;
    }

    /// <summary>An issued row is taken back (its withdrawal wasn't committed): it's gone from its holder's inventory and the cold store.</summary>
    internal static void Unissue(LiveItems.LiveItem record, string why)
    {
        if (record == null || !IsDataOnly(record))
            return;
        Multiplayer.LogWarning($"Inventory: {record.PrefabName} (netId {record.NetId}) is taken back out of slot {record.Slot}: {why}");
        if (HostInventory.Holds(record.NetId) && InventoryStandIn.TryGet(record.NetId, out InventoryStandIn standIn))
        {
            VanillaInventorySlots.Forget(DV.InventorySystem.Inventory.Instance, standIn.gameObject);
            standIn.Retire();
        }
        ColdInventories.Remove(record.PersistentItemId);
        DataGoesCold(record, null);
    }

    // A data-only item's Create, from its record alone: there is no object to write it.
    private static ItemUpdate CreateOf(LiveItems.LiveItem record)
    {
        ItemUpdate create = new()
        {
            UpdateType = ItemUpdate.ItemUpdateType.Create,
            ItemNetId = record.NetId,
            PrefabName = record.PrefabName,
            ItemState = ItemState.InInventory,
            BelongsToPlayer = record.Owner != Guid.Empty
        };
        LiveItems.WriteToSnapshot(record, create, ItemTransitionReason.HostLocalState);
        return create;
    }
}
