// Ledger steps (docs/design/items/ledger.md): what the ledger has for a player, where the server
// has an inventory item, and the checks every run ends with: every item has one home, and no slot
// holds an object where its row should be. Each [Step] runs inside a game: call it through
// pair.Host / pair.Client.
using System;
using System.Collections.Generic;
using System.Linq;
using Dvmp.Agent;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>Where the server has an inventory item: its record's slot, and the ledger's home and claim.</summary>
    public sealed class ServerSlot
    {
        public bool Known;
        public string Placement = "", Home = "";
        public int Slot = -1, LedgerSlot = -1, Claim = -1;

        public override string ToString() => Known
            ? $"{Placement} slot {Slot}; ledger {Home}{(Claim >= 0 ? ", claims " + Claim : "")}"
            : "no record";
    }

    public static class Ledger
    {
        /// <summary>Host: the ledger's lines for an owner (an identity), or for everyone ("").</summary>
        [Step]
        public static string Dump(string owner) =>
            ServerLedger.Ledger.Dump(Guid.TryParse(owner, out Guid who) ? OwnerRef.Player(who) : default);

        /// <summary>Host: where the server has the item: its record's slot and the ledger's home.</summary>
        [Step]
        public static ServerSlot Where(ushort netId)
        {
            if (!LiveItems.TryGet(netId, out LiveItems.LiveItem record)) return new ServerSlot();
            ServerSlot slot = new() { Known = true, Placement = record.Placement.ToString(), Slot = record.Slot };
            if (ServerLedger.Ledger.TryGet(record.PersistentItemId, out Multiplayer.Core.Items.Ledger.LedgerEntry entry))
            {
                slot.Home = entry.IsLive ? entry.Home.ToString() : "ended " + entry.Ended.Reason;
                slot.LedgerSlot = entry.Home.Kind == HomeKind.Slot ? entry.Home.Slot : -1;
                slot.Claim = entry.ClaimSlot;
            }
            return slot;
        }

        /// <summary>Host: the run removes this item afterwards, as one its spawn steps made (an item
        /// made again under a new net ID, after a rejoin).</summary>
        [Step]
        public static void Track(ushort netId) => Items.Track(netId);

        /// <summary>This game: the slot its inventory shows the item in, or -1.</summary>
        [Step]
        public static int LocalSlot(ushort netId) => PlayerInventory.Slot(netId);
    }

    public static class Invariant
    {
        /// <summary>Host: what breaks the one-home check now (HomeInvariant); empty when it holds.</summary>
        [Step]
        public static string[] Check() => ServerLedger.CheckNow().ToArray();

        /// <summary>
        /// This game: the inventory slots that hold an item's object where its row should be (D14,
        /// docs/design/items/inventory.md, "Stand-ins"): an object its row holder would put away as
        /// its stand-in, or one with no NetId. A VR belt item in a belt slot, a loaded magazine, a
        /// job paper whose job isn't known here and an item with no prefab here stay objects. Empty
        /// on a game whose inventory isn't rows (a dedicated host, a client out of a session).
        /// </summary>
        [Step]
        public static string[] ObjectsInSlots()
        {
            IInventoryRowHolder holder = InventoryRowHolder.Current;
            DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
            if (holder == null || inventory == null) return Array.Empty<string>();
            List<string> broken = new();
            foreach ((ushort netId, int slot, GameObject go) in PlayerInventory.Stored())
            {
                if (go.GetComponent<InventoryStandIn>() != null) continue;
                NetworkedItem item = go.GetComponent<NetworkedItem>();
                if (item == null || netId == 0)
                    broken.Add($"slot {slot}: {go.name} is an object with no NetId");
                else if (inventory.IsValidVRBeltIndex(slot) && InventoryStandIn.IsBeltSnappablePrefab(PlayerInventory.PrefabOf(go)))
                    continue;
                else if (holder.PutsAwayAsStandIn(item))
                    broken.Add($"slot {slot}: {PlayerInventory.PrefabOf(go)} (netId {netId}) is an object, not its stand-in");
            }
            return broken.ToArray();
        }
    }
}
