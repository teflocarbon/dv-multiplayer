using DV.InventorySystem;
using Multiplayer.Core.Items.Access;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.Physics;
using Multiplayer.Server.Items.Access;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.ChangeCauses;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items.LiveState;

// Records for items the server made: from cold storage, as data only, as ammo in a tool.
public static partial class LiveItems
{
    /// <summary>
    /// Registers an item materialized from host-owned detached storage without allowing
    /// the current holder to accidentally replace its persistent owner. <paramref name="cause"/> is
    /// how it came: a withdrawal when null, or the server making it (a player's starting items).
    /// </summary>
    public static LiveItem RegisterMaterializedColdItem(NetworkedItem item, ServerPlayer holder,
        Guid owner, ItemUpdate snapshot, Guid persistentItemId,
        ChangeCause<ItemMoveReason>? cause = null)
    {
        if (item == null || holder == null || snapshot == null || item.NetId == 0)
            return null;
        // The item keeps its id through the container (owned-items.md, "Known bugs"): without it the
        // record wasn't saved.
        persistentItemId = ServerLedger.ClaimPersistentId(item, persistentItemId);

        // Into the holder's inventory, or, dropped, into the world at the snapshot's position (an
        // issued essential that found the inventory full, on its way to Lost and Found).
        bool dropped = snapshot.ItemState == ItemState.Dropped;
        LiveItem record = new()
        {
            NetId = item.NetId,
            Revision = 1,
            Placement = dropped ? ItemPlacementKind.World : ItemPlacementKind.PlayerInventory,
            Holder = dropped ? Guid.Empty : holder.Guid,
            StoredOwner = owner,
            InventoryClaimFlags = snapshot.InventoryClaimFlags,
            PrefabName = item.Item?.InventorySpecs?.ItemPrefabName ?? item.name,
            Position = snapshot.ItemPosition,
            Rotation = snapshot.ItemRotation,
            LastReason = ItemTransitionReason.HostLocalState,
            PersistentItemId = persistentItemId,
            AuthoredItemKey = item.AuthoredItemKey ?? string.Empty
        };
        records[item.NetId] = record;
        // Its own slot: the one the snapshot names, else its claim, else the lowest free one.
        if (!dropped)
            record.Slot = ServerLedger.FreeSlot(record, holder.Guid, snapshot.InventorySlot, snapshot.InventoryClaimSlot);
        ChangeCause<ItemMoveReason> withdrawn = cause ?? ItemChangeLog.Move(ItemMoveReason.WithdrawnFromContainer, holder,
            ItemChangeCauses.Detail(ItemChangeLog.Describe(
                    new ItemWhereabouts(LivePlacement.Container, Guid.Empty, owner),
                    ItemChangeLog.Whereabouts(record)),
                "slot " + record.Slot));
        ServerLedger.Settle(record, owner, snapshot.InventoryClaimSlot, withdrawn);
        WriteToSnapshot(record, snapshot, record.LastReason);
        snapshot.MoveCause = withdrawn;
        ItemChangeLog.RecordMove(item, record, withdrawn);
        item.ApplyAuthorityMetadata(snapshot);
        Publish("item.cold-materialization-registered", item, record, new()
        {
            ["holderPlayerId"] = holder.PlayerId,
            ["persistentOwnerPlayerId"] = PlayerNumbers.Of(owner)
        });
        return record;
    }

    /// <summary>
    /// Registers a joiner's inventory item as data only from the start (OI-COLD-03, OI-COLD-06): a
    /// live record under a reserved NetId, in <paramref name="slot"/> of <paramref name="holder"/>'s
    /// inventory, with no object on the server. Its row stays in the cold store; the holder's game
    /// makes the only object from it.
    /// </summary>
    internal static LiveItem RegisterDataOnly(ushort netId, ServerPlayer holder, SavedItemRow row, int slot,
        ChangeCause<ItemMoveReason> cause, Func<LiveItem, string> own = null)
    {
        if (netId == 0 || holder == null || row == null || row.PersistentItemId == Guid.Empty || slot < 0)
            return null;
        LiveItem record = new()
        {
            NetId = netId,
            Revision = 1,
            Placement = ItemPlacementKind.PlayerInventory,
            Holder = holder.Guid,
            Slot = slot,
            StoredOwner = row.OwnerIdentity,
            InventoryClaimFlags = row.ClaimFlags,
            PrefabName = row.PrefabName,
            LastReason = ItemTransitionReason.HostLocalState,
            PersistentItemId = row.PersistentItemId,
            AuthoredItemKey = string.Empty
        };
        records[netId] = record;
        ServerLedger.BindNetId(row.PersistentItemId, netId);
        // Born as the caller says (a grant), or settled where it now is (born there if new).
        string refused = own?.Invoke(record) ?? string.Empty;
        if (own == null)
            ServerLedger.Settle(record, row.OwnerIdentity, row.ClaimSlot, cause);
        else if (refused.Length > 0)
            Multiplayer.LogWarning($"item.ledger-refused: {row.PrefabName} (netId {netId}) wasn't made {ItemChangeLog.PlayerName(row.OwnerIdentity)}'s: {refused}");
        return record;
    }

    /// <summary>
    /// Server: ammo kept as data in the magazine of a tool kept as data (PlayerItems.Ammo), under a
    /// NetId reserved for it. Its ledger entry, if owned, is already moved to the magazine.
    /// </summary>
    internal static LiveItem RegisterDataOnlyAmmo(ushort netId, LiveItem tool, SavedItemRow row, int slot)
    {
        if (netId == 0 || tool == null || row == null || row.PersistentItemId == Guid.Empty || slot < 0)
            return null;
        LiveItem record = new()
        {
            NetId = netId,
            Revision = 1,
            Placement = ItemPlacementKind.Magazine,
            MagazineNetId = tool.NetId,
            MagazineToolId = tool.PersistentItemId,
            MagazineSlot = slot,
            StoredOwner = row.OwnerIdentity,
            PrefabName = row.PrefabName,
            LastReason = ItemTransitionReason.HostLocalState,
            PersistentItemId = row.PersistentItemId,
            AuthoredItemKey = string.Empty
        };
        records[netId] = record;
        ServerLedger.BindNetId(row.PersistentItemId, netId);
        return record;
    }
}
