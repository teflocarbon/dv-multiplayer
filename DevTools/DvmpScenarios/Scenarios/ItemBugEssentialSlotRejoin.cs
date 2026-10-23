using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Known item bug (expected to fail on beta, docs/research/item-bug-review.md): a player
    /// rearranges their inventory, moving an essential (the comms radio, slot 0) to another slot
    /// and an ordinary item into the essential's old slot, then rejoins. Every essential must be
    /// back in a slot of its own, visible, and no two items may share a slot. The server never
    /// hears of a move inside the inventory, so it still has the radio's claim on its old slot.
    /// </summary>
    [Scenario("item-bug-essential-slot-rejoin", "Known bug: an essential moved to another slot, an ordinary item put in its old slot, and a rejoin: every essential must come back in a slot of its own")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class ItemBugEssentialSlotRejoin : Scenario<ItemBugEssentialSlotRejoin.Options>
    {
        public sealed class Options
        {
            /// <summary>The essential's slot (0, the comms radio).</summary>
            public int Slot;
            /// <summary>Kill the client instead of quitting.</summary>
            public bool Crash;
            /// <summary>Whose inventory: "client", or "host" (the host's own player; needs RESTART=1 and a player host).</summary>
            public string Who = "client";
            /// <summary>The host saves and both games restart from the save, instead of a rejoin.</summary>
            public bool Restart;
            /// <summary>The save to reload with RESTART=1.</summary>
            public string Session = "DVMP Test Career 2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            bool host = o.Who == "host";
            if (host && (pair.Dedicated || !o.Restart)) throw new HarnessException("WHO=host needs a player host and RESTART=1");
            string how = o.Restart ? "restart" : "rejoin";
            // An ordinary item of the player's own (a purchase gives one), so the run needs nothing in the inventory.
            ushort lantern = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Lantern", o.Who));
            await (host ? pair.Host : pair.Client).Run<int>(s => Items.Stash(s, lantern, false), 60);
            Layout before = await (host ? pair.Host : pair.Client).Run<Layout>(s => InGame.Rearrange(s, o.Slot, lantern));
            Report.Note($"{o.Who}: {before.Essential} {before.EssentialId} moved {o.Slot}->{before.EssentialSlot}, {before.Other} {before.OtherId} moved into slot {o.Slot}");
            string record = await pair.Host.Run(() => InGame.ServerClaim(before.EssentialId));
            Report.Note($"server record of {before.Essential}: {record}");

            if (o.Restart)
            {
                await pair.Host.Run(() => Saves.Manual());
                await pair.Restart(o.Session);
            }
            else
            {
                if (o.Crash) await pair.Client.Kill();
                else
                {
                    await pair.Client.Quit();
                    await Task.Delay(4000);
                }
                await pair.Rejoin();
            }
            await Task.Delay(12000);   // the client receives its restored items

            Check after = await (host ? pair.Host : pair.Client).Run(() => InGame.Inspect());
            Report.Note($"after the {how}: {after.Layout}");
            pair.Cleanup(() => (host ? pair.Host : pair.Client).Run(() => InGame.PutBack(after.RadioId, o.Slot)));
            Expect.Empty(after.Failures, $"every owned item in a slot of its own after the {how}, every essential visible");
        }

        public sealed class Layout
        {
            public string Essential, Other;
            public ushort EssentialId, OtherId;
            public int EssentialSlot;
        }

        public sealed class Check
        {
            public string Layout;
            public string[] Failures;
            /// <summary>The comms radio this player owns now (net IDs change across a restart).</summary>
            public ushort RadioId;
        }

        // ---- runs inside the games
        public static class InGame
        {
            static string Name(GameObject go) => go.name.Replace("(Clone)", "");

            /// <summary>The player (client or the host's own) moves the essential in
            /// <paramref name="slot"/> to the last free slot and the ordinary item
            /// <paramref name="otherId"/> into <paramref name="slot"/>, as dragging them in the
            /// inventory screen does. Checks everything before it moves anything.</summary>
            [Step]
            public static IEnumerator Rearrange(Step<Layout> step, int slot, ushort otherId)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                if (Hands.Held != null) yield return Hands.Drop();
                GameObject essential = inv.PeekItemAtSlot(slot, false) ?? throw new Exception("slot " + slot + " is empty");
                if (essential.GetComponent<InventoryItemSpec>()?.IsEssential != true) throw new Exception(Name(essential) + " in slot " + slot + " isn't essential: a run before left the inventory rearranged");
                NetworkedItem.TryGet(otherId, out NetworkedItem otherItem);
                GameObject other = otherItem?.gameObject;
                int otherSlot = other != null ? inv.IndexOf(other) : -1;
                if (otherSlot < 0) throw new Exception($"item {otherId} isn't in the inventory");
                int free = inv.GetLastFreeSlot();
                if (free < 0 || free == otherSlot) throw new Exception("no free slot");
                if (!inv.MoveItemFromTo(slot, free, false)) throw new Exception("couldn't move the essential to slot " + free);
                yield return new WaitForSeconds(1);
                if (!inv.MoveItemFromTo(otherSlot, slot, false)) throw new Exception($"couldn't move {Name(other)} from {otherSlot} to {slot}");
                yield return new WaitForSeconds(3);
                if (inv.IndexOf(essential) != free || inv.IndexOf(other) != slot) throw new Exception("the moves didn't stick");
                step.Result = new Layout
                {
                    Essential = Name(essential), EssentialId = essential.GetComponent<NetworkedItem>().NetId, EssentialSlot = free,
                    Other = Name(other), OtherId = other.GetComponent<NetworkedItem>().NetId
                };
            }

            /// <summary>Host: the server's record of the item, its placement and claim.</summary>
            [Step]
            public static string ServerClaim(ushort id) =>
                LiveItems.TryGet(id, out var r)
                    ? $"{r.Placement} of {r.Holder}, claim slot {r.InventoryClaimSlot} ({r.InventoryClaimFlags})"
                    : "no record";

            /// <summary>Client: every item this player owns that the game has in the inventory is in
            /// exactly one slot, alone, and active; every essential it owns is in a slot. Result: the
            /// layout and what's wrong.</summary>
            [Step]
            public static Check Inspect()
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                byte me = NetworkLifecycle.Instance.LocalSession.PlayerId;
                var failures = new List<string>();
                var bySlot = new Dictionary<int, GameObject>();
                var layout = new List<string>();
                for (int s = 0; s < inv.Capacity; s++)
                {
                    GameObject go = inv.PeekItemAtSlot(s, true);
                    if (go == null) continue;
                    bySlot[s] = go;
                    layout.Add($"{s}:{Name(go)}{(inv.GetSlotDroppedState(s) ? "(silhouette)" : "")}");
                }
                var mine = Dev.All<NetworkedItem>().Where(n => n != null && n.NetId != 0 && n.Relation == ItemRelation.Mine).ToList();
                foreach (var n in mine)
                {
                    bool essential = n.GetComponent<InventoryItemSpec>()?.IsEssential == true;
                    int slot = inv.IndexOf(n.gameObject);
                    var state = n.GetItemState();
                    if (state == ItemState.InInventory && slot < 0)
                        failures.Add($"{Name(n.gameObject)} {n.NetId} is InInventory but in no slot (claim {n.InventoryClaimSlot}, active {n.gameObject.activeInHierarchy})");
                    else if (state == ItemState.InInventory && !bySlot.TryGetValue(slot, out var at))
                        failures.Add($"{Name(n.gameObject)} {n.NetId} says slot {slot}, which shows nothing");
                    else if (state == ItemState.InInventory && bySlot[slot] != n.gameObject)
                        failures.Add($"{Name(n.gameObject)} {n.NetId} shares slot {slot} with {Name(bySlot[slot])}");
                    if (essential && slot < 0)
                        failures.Add($"essential {Name(n.gameObject)} {n.NetId} is in no slot: {state}, active {n.gameObject.activeInHierarchy}, claim {n.InventoryClaimSlot}");
                }
                // DV's own essentials: one of each, owned or not, must exist for this player.
                foreach (string prefab in new[] { "CommsRadio", "Map", "MapSchematic", "RouteMap", "wallet", "VehicleCatalog", "Compass" })
                {
                    int count = mine.Count(n => Name(n.gameObject) == prefab);
                    if (count != 1) failures.Add($"{prefab}: {count} owned, expected 1");
                }
                return new Check
                {
                    Layout = string.Join(" ", layout), Failures = failures.ToArray(),
                    RadioId = mine.FirstOrDefault(n => Name(n.gameObject) == "CommsRadio")?.NetId ?? 0
                };
            }

            /// <summary>Client, cleanup: the essential back in its own slot (whatever is there moves
            /// to the last free slot first).</summary>
            [Step]
            public static IEnumerator PutBack(ushort id, int slot)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                if (!NetworkedItem.TryGet(id, out NetworkedItem n) || n == null) yield break;
                int now = inv.IndexOf(n.gameObject);
                if (now == slot || now < 0) yield break;
                if (inv.PeekItemAtSlot(slot, true) != null) inv.MoveItemFromTo(slot, inv.GetLastFreeSlot(), false);
                inv.MoveItemFromTo(now, slot, false);
                yield return new WaitForSeconds(2);
            }
        }
    }
}
