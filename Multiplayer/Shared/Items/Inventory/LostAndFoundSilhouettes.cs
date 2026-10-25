using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.Inventory;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// An essential of the local player's in Lost and Found keeps its silhouette in its claim slot,
/// which the server keeps reserved for it (docs/design/items/inventory.md, "Silhouettes"). The
/// entry is data on the server with no object on this game, so the silhouette is made from its Lost
/// and Found row, by the row's claim slot and NetId (<see cref="ItemRow.ClaimSlot"/>,
/// <see cref="ItemRow.ItemNetId"/>): the item's stand-in, dropped and reserved, as one made from an
/// object is (<see cref="InventorySilhouettes"/>). A recall from it, the retrieval's update and an
/// object of the item made later all find it by its NetId. It goes when its row goes (a retrieval,
/// a recall, an entry that went), unless the update that put the item back has made it the item's
/// stand-in in its slot already. The same on a client and for a player host's own player: both
/// get their list from the server (<c>LostAndFoundList</c>).
/// </summary>
internal static class LostAndFoundSilhouettes
{
    private const float RetrySeconds = 1f;

    // The local player's lost essentials, by NetId, as the server last listed them.
    private static readonly Dictionary<ushort, ItemRow> listed = new();
    // The silhouettes made here from rows.
    private static readonly HashSet<ushort> made = new();
    // Rows whose slot something else filled, logged once each.
    private static readonly HashSet<ushort> blocked = new();
    private static bool pending;
    private static float nextTry;

    /// <summary>The local player's Lost and Found as the server sent it: each lost essential's silhouette is made, and one whose row went goes.</summary>
    public static void Apply(IEnumerable<ItemRow> rows)
    {
        listed.Clear();
        foreach (ItemRow row in rows ?? Enumerable.Empty<ItemRow>())
            if (row != null && row.ClaimSlot >= 0 && row.ItemNetId != 0)
                listed[row.ItemNetId] = row;
        blocked.RemoveWhere(netId => !listed.ContainsKey(netId));
        pending = true;
        nextTry = 0f;
        Update();
    }

    /// <summary>The session's list is gone; its stand-ins go with the rest (<see cref="InventoryStandIn.RetireAll"/>).</summary>
    public static void Clear()
    {
        listed.Clear();
        made.Clear();
        blocked.Clear();
        pending = false;
    }

    /// <summary>
    /// Every frame: a new list at once, once this game's inventory is stand-ins (the join waits
    /// for it); else once a second while there are rows, for a silhouette another path took away (an
    /// object of the item made for a request that was then refused) or whose slot something else
    /// held (an item the server is still correcting out of it).
    /// </summary>
    public static void Update()
    {
        if (!pending && (listed.Count == 0 && made.Count == 0 || Time.realtimeSinceStartup < nextTry))
            return;
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        if (inventory == null || InventoryRowHolder.Current == null)
            return;
        pending = false;
        nextTry = Time.realtimeSinceStartup + RetrySeconds;
        foreach (ushort netId in made.Where(netId => !listed.ContainsKey(netId)).ToArray())
            Drop(inventory, netId);
        foreach (ItemRow row in listed.Values)
            Show(inventory, row);
    }

    // The row's silhouette in its claim slot, unless the item is in a slot here already or the slot
    // holds something else (tried again later).
    private static void Show(DV.InventorySystem.Inventory inventory, ItemRow row)
    {
        ushort netId = row.ItemNetId;
        int slot = row.ClaimSlot;
        // The item is in a slot here already, as a stand-in (a silhouette, or put back) or as the
        // object a tombstone keeps with its own silhouette.
        if (InventoryStandIn.TryGet(netId, out InventoryStandIn existing) &&
            VanillaInventorySlots.SlotOf(inventory, existing.gameObject) is int at && at >= 0)
        {
            // One made here whose claim the ledger has since moved (the row says where) moves too.
            if (at == slot || !made.Contains(netId) || !InventorySilhouettes.IsSilhouette(inventory, existing))
                return;
            Drop(inventory, netId);
        }
        if (NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null &&
            VanillaInventorySlots.SlotOf(inventory, item.gameObject) >= 0)
            return;
        if (!InventorySlots.IsValid(slot, inventory.Capacity) || !InventoryStandIn.CanStandIn(row.PrefabName))
        {
            if (blocked.Add(netId))
                Multiplayer.LogWarning($"Inventory: no silhouette for {row.PrefabName} (netId {netId}) in Lost and Found: " +
                                       $"claim slot {slot} or its prefab isn't one this game can show");
            return;
        }
        GameObject occupant = VanillaInventorySlots.At(inventory, slot);
        if (occupant != null)
        {
            if (blocked.Add(netId))
                Multiplayer.LogWarning($"Inventory: {row.PrefabName} (netId {netId}) in Lost and Found claims slot {slot}, " +
                                       $"which holds {occupant.name}; its silhouette waits for the slot");
            return;
        }
        InventoryStandIn standIn = InventoryStandIn.For(netId, row.PrefabName);
        standIn.Relation = ItemRelation.Mine;
        standIn.ClaimSlot = slot;
        standIn.ClaimFlags = ItemInventoryClaimFlags.Reserved | ItemInventoryClaimFlags.Dropped;
        standIn.Revision = row.Revision;
        int added;
        // Added as dropped, it's no put-away (InventoryPutAwayPatch) and asks the server nothing.
        using (InventoryIntegration.BeginAuthoritativePlacement())
            added = inventory.AddItemToInventory(standIn.gameObject, slot, true);
        if (added != slot || !InventorySilhouettes.IsSilhouette(inventory, standIn))
        {
            VanillaInventorySlots.Forget(inventory, standIn.gameObject);
            standIn.Retire();
            if (blocked.Add(netId))
                Multiplayer.LogWarning($"Inventory: vanilla didn't take {row.PrefabName}'s silhouette (netId {netId}) " +
                                       $"into slot {slot} (added at {added})");
            return;
        }
        blocked.Remove(netId);
        made.Add(netId);
        Trace("item.silhouette-from-lost-row", netId, new()
        {
            ["slot"] = slot,
            ["prefabName"] = row.PrefabName ?? string.Empty,
            ["revision"] = row.Revision
        });
    }

    // Its row went. A silhouette still standing for it goes; a stand-in the item's update put back
    // in its slot is the item's now, and stays.
    private static void Drop(DV.InventorySystem.Inventory inventory, ushort netId)
    {
        made.Remove(netId);
        if (!InventoryStandIn.TryGet(netId, out InventoryStandIn standIn) ||
            !InventorySilhouettes.IsSilhouette(inventory, standIn))
            return;
        int slot = VanillaInventorySlots.Forget(inventory, standIn.gameObject);
        standIn.Retire();
        Trace("item.silhouette-from-lost-row-dropped", netId, new() { ["slot"] = slot });
    }

    private static void Trace(string eventName, ushort netId, Dictionary<string, object> data)
    {
        if (DebugRuntime.EnabledFor("item"))
            DebugRuntime.Publish("item", eventName, NetworkRole.DebugSide, entityType: "Item", entityId: netId.ToString(), data: data);
    }
}
