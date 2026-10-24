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
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
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
            states.Add(row.State?.ToString(Formatting.None) ?? string.Empty);
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

    // One row goes live as data: a record in its slot under a reserved NetId, its row kept. Job
    // papers (their job's objects, OI-COLD-07), an item the ledger moved to Lost and Found, and one
    // with no free slot are made as objects instead (GoLive), as before. Returns its Create, or null.
    private static ItemUpdate TakeUp(ServerPlayer player, SavedItemRow row)
    {
        bool lost = ServerLedger.Ledger.TryGetHome(row.PersistentItemId, out Home home) && home.Kind == HomeKind.LostAndFound;
        int slot = lost ? -1
            : ServerLedger.FreeSlot(row.PersistentItemId, row.OwnerIdentity, row.ClaimSlot, player.Guid, row.Slot, row.ClaimSlot);
        if (ItemSaveLoad.IsJobSystemDocument(row.PrefabName) || slot < 0 || ItemSaveLoad.FindSpec(row.PrefabName) == null)
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
