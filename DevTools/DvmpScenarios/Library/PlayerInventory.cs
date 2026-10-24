// The local player's inventory as the scenarios see it (docs/design/items/inventory.md, "Stand-ins";
// D14): a slot holds the item's stand-in (InventoryStandIn, the row) and no object, on a remote client and
// for a player host's own player; an object (a VR belt item, job papers) can be in one too. These read
// and act on either, by NetId.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dvmp.Agent;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    public static class PlayerInventory
    {
        private static DV.InventorySystem.Inventory Inv => DV.InventorySystem.Inventory.Instance;

        /// <summary>What vanilla keeps for the item in this game's inventory: its stand-in, else its object, else null.</summary>
        public static GameObject SlotObject(ushort netId)
        {
            if (InventoryStandIn.TryGet(netId, out InventoryStandIn standIn)) return standIn.gameObject;
            return NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null ? item.gameObject : null;
        }

        /// <summary>The item in a slot, by NetId (its stand-in's or its object's), or 0.</summary>
        public static ushort NetIdAt(int slot, bool includeDropped = false)
        {
            GameObject go = Inv?.PeekItemAtSlot(slot, includeDropped);
            return NetIdOf(go);
        }

        /// <summary>The NetId an inventory entry stands for, or 0.</summary>
        public static ushort NetIdOf(GameObject go)
        {
            if (go == null) return 0;
            InventoryStandIn standIn = go.GetComponent<InventoryStandIn>();
            if (standIn != null) return standIn.NetId;
            return go.GetComponent<NetworkedItem>()?.NetId ?? 0;
        }

        /// <summary>The prefab of an inventory entry (a stand-in's or an object's).</summary>
        public static string PrefabOf(GameObject go)
        {
            if (go == null) return "";
            InventoryStandIn standIn = go.GetComponent<InventoryStandIn>();
            if (standIn != null) return standIn.PrefabName;
            return go.GetComponent<InventoryItemSpec>()?.ItemPrefabName ?? go.name;
        }

        /// <summary>Whether an inventory entry is an item with this component (a stand-in: its prefab has it).</summary>
        public static bool Has<T>(GameObject go) where T : Component =>
            go != null && (go.GetComponent<T>() != null || go.GetComponent<InventoryStandIn>()?.Prefab?.GetComponent<T>() != null);

        /// <summary>Whether an inventory entry is an essential (its spec's flag).</summary>
        public static bool IsEssential(GameObject go) =>
            go != null && (go.GetComponent<InventoryItemSpec>()?.IsEssential ?? go.GetComponent<InventoryStandIn>()?.IsEssential ?? false);

        /// <summary>The slot this game's inventory shows the item in (a silhouette too), or -1.</summary>
        [Step]
        public static int Slot(ushort netId)
        {
            GameObject go = SlotObject(netId);
            return go != null && Inv != null ? Inv.IndexOf(go) : -1;
        }

        /// <summary>Whether the item is stored in this game's inventory: in a slot, not a silhouette.</summary>
        [Step]
        public static bool Holds(ushort netId)
        {
            GameObject go = SlotObject(netId);
            return go != null && Inv != null && Inv.Contains(go, false);
        }

        /// <summary>Whether this game keeps the item as a stand-in (a row with no object).</summary>
        [Step]
        public static bool IsStandIn(ushort netId) => InventoryStandIn.TryGet(netId, out _);

        /// <summary>The server revision this game has for the item (its stand-in's, else its object's), for a request.</summary>
        public static uint Revision(ushort netId)
        {
            if (InventoryStandIn.TryGet(netId, out InventoryStandIn standIn)) return standIn.Revision;
            return NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null ? item.AuthorityRevision : 0;
        }

        /// <summary>Swaps two items' slots, as dragging one onto the other does. Result: whether the game swapped them.</summary>
        public static bool Swap(ushort a, ushort b)
        {
            int from = Slot(a), to = Slot(b);
            return from >= 0 && to >= 0 && Inv.SwapItems(from, to);
        }

        /// <summary>Moves the item to a slot, as dragging it there does. Result: whether it's there.</summary>
        public static bool MoveTo(ushort netId, int slot)
        {
            int from = Slot(netId);
            return from == slot || from >= 0 && Inv.MoveItemFromTo(from, slot, false);
        }

        /// <summary>
        /// NetIds of the items stored in this game's inventory (not silhouettes), stand-ins and
        /// objects alike, with their slots.
        /// </summary>
        public static IEnumerable<(ushort netId, int slot, GameObject go)> Stored()
        {
            if (Inv == null) yield break;
            for (int s = 0; s < Inv.Capacity; s++)
            {
                GameObject go = Inv.PeekItemAtSlot(s, false);
                if (go != null) yield return (NetIdOf(go), s, go);
            }
        }

        /// <summary>One of this player's own items as this game has it: an object, or a stand-in (state InInventory).</summary>
        public sealed class OwnItem
        {
            public ushort NetId;
            public string Name;
            public ItemState State;
            public int Slot;
            public bool StandIn;
        }

        /// <summary>Every item this game has as the local player's own (relation Mine), objects and stand-ins.</summary>
        public static List<OwnItem> Mine()
        {
            var mine = Dev.All<NetworkedItem>().Where(n => n != null && n.NetId != 0 && n.Relation == ItemRelation.Mine)
                .Select(n => new OwnItem
                {
                    NetId = n.NetId, Name = n.name.Replace("(Clone)", ""), State = n.GetItemState(),
                    Slot = Inv != null ? Inv.IndexOf(n.gameObject) : -1
                }).ToList();
            mine.AddRange(InventoryStandIn.All.Where(s => s.Relation == ItemRelation.Mine && mine.All(m => m.NetId != s.NetId))
                .Select(s => new OwnItem
                {
                    NetId = s.NetId, Name = s.PrefabName, State = ItemState.InInventory,
                    Slot = Inv != null ? Inv.IndexOf(s.gameObject) : -1, StandIn = true
                }));
            return mine;
        }

        /// <summary>Takes the item out of its slot into the player's hand, as the UI's equip does,
        /// and waits for its object to be held. Throws if it isn't in the inventory.</summary>
        public static IEnumerator TakeOut(ushort netId)
        {
            GameObject go = SlotObject(netId);
            int slot = go != null ? Inv.IndexOf(go) : -1;
            if (slot < 0) throw new Exception($"item {netId} isn't in the inventory to take out");
            Inv.EquipItem(go, slot, 0);
            yield return Wait.Until(() => NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null &&
                                          item.Item != null && item.Item.IsGrabbed(), 10f, $"item {netId} in the hand");
        }

        /// <summary>Drops the item out of its slot (or the hand) at the player's feet, as the UI's drop does.</summary>
        public static GameObject Drop(ushort netId)
        {
            GameObject go = SlotObject(netId);
            return go != null ? Inv.DropItemFromHandsOrInventory(go) : null;
        }

        /// <summary>Asks the server to recall the item into a slot, as clicking its silhouette does: by its
        /// object, else by its stand-in's row (an item in Lost and Found has no object here). Result:
        /// whether a request went out.</summary>
        public static bool Recall(ushort netId, int slot)
        {
            NetworkedItemManager manager = NetworkedItemManager.Instance;
            if (manager == null) return false;
            if (NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null) return manager.RequestItemRecall(item, slot);
            return InventoryStandIn.TryGet(netId, out InventoryStandIn standIn) &&
                   manager.RequestItemRecall(netId, standIn.Revision, standIn.Relation, slot);
        }

        /// <summary>Moves whatever is in a slot to another, as dragging it in the UI does.</summary>
        public static bool Move(int from, int to) => Inv.MoveItemFromTo(from, to, false);
    }
}
