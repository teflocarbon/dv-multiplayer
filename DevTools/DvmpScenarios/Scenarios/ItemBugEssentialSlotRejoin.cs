using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A player rearranges their inventory, moving an essential (the comms radio, slot 0) to
    /// another slot and an ordinary item into the essential's old slot, then rejoins
    /// (docs/research/item-bug-review.md). Every essential must be back in a slot of its own,
    /// visible, and no two items may share a slot. Before R3 the server never heard of a move
    /// inside the inventory and kept the radio's claim on its old slot; now each move is a request
    /// the ledger commits (OI-SLOT-02), so the rejoin finds every item where the player left it.
    /// </summary>
    [Scenario("item-bug-essential-slot-rejoin", "An essential moved to another slot, an ordinary item put in its old slot, and a rejoin: every essential comes back in a slot of its own")]
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
            // Back in its slot if the run fails before the rejoin too: the pair's later runs expect it there.
            ushort essential = await (host ? pair.Host : pair.Client).Run(() => InGame.At(o.Slot));
            // Only until the one after the rejoin takes over: cleanups run last first, and after a restart
            // the old NetId can name another item.
            bool afterCleanup = false;
            pair.Cleanup(() => afterCleanup ? Task.CompletedTask : (host ? pair.Host : pair.Client).Run(() => InGame.PutBack(essential, o.Slot)));
            Layout before = await (host ? pair.Host : pair.Client).Run<Layout>(s => InGame.Rearrange(s, o.Slot, lantern));
            Report.Note($"{o.Who}: {before.Essential} {before.EssentialId} moved {o.Slot}->{before.EssentialSlot}, {before.Other} {before.OtherId} moved into slot {o.Slot}");
            string record = await pair.Host.Run(() => InGame.ServerClaim(before.EssentialId));
            Report.Note($"server record of {before.Essential}: {record}");
            ServerSlot essentialAt = await pair.Host.Run(() => Ledger.Where(before.EssentialId));
            ServerSlot otherAt = await pair.Host.Run(() => Ledger.Where(before.OtherId));
            Expect.Equal(before.EssentialSlot, essentialAt.Slot, $"the server's slot for the {before.Essential}, before the {how}");
            Expect.Equal(o.Slot, otherAt.Slot, $"the server's slot for the {before.Other}, before the {how}");

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
            afterCleanup = true;
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
                if (!PlayerInventory.IsEssential(essential)) throw new Exception(Name(essential) + " in slot " + slot + " isn't essential: a run before left the inventory rearranged");
                ushort essentialId = PlayerInventory.NetIdOf(essential);
                // On a client the slots hold stand-ins (D14), which keep their place through a move.
                GameObject other = PlayerInventory.SlotObject(otherId);
                int otherSlot = PlayerInventory.Slot(otherId);
                if (otherSlot < 0) throw new Exception($"item {otherId} isn't in the inventory");
                int free = inv.GetLastFreeSlot();
                if (free < 0 || free == otherSlot) throw new Exception("no free slot");
                if (!inv.MoveItemFromTo(slot, free, false)) throw new Exception("couldn't move the essential to slot " + free);
                yield return new WaitForSeconds(1);
                if (!inv.MoveItemFromTo(otherSlot, slot, false)) throw new Exception($"couldn't move {Name(other)} from {otherSlot} to {slot}");
                yield return new WaitForSeconds(3);
                // By NetId: a slot's object and its stand-in swap places (D14), so the objects read
                // before the moves may not be the ones in the slots now.
                int essentialAt = PlayerInventory.Slot(essentialId), otherAt = PlayerInventory.Slot(otherId);
                if (essentialAt != free || otherAt != slot)
                    throw new Exception($"the moves didn't stick: {Name(essential)} in slot {essentialAt}, not {free}; {Name(other)} in slot {otherAt}, not {slot}");
                step.Result = new Layout
                {
                    Essential = Name(essential), EssentialId = essentialId, EssentialSlot = free,
                    Other = Name(other), OtherId = otherId
                };
            }

            /// <summary>Host: the server's record of the item, its placement and claim.</summary>
            [Step]
            public static string ServerClaim(ushort id) =>
                LiveItems.TryGet(id, out var r)
                    ? $"{r.Placement} of {r.Holder}, slot {r.Slot}, claim slot {r.InventoryClaimSlot} ({r.InventoryClaimFlags})"
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
                // Stored items on a client are stand-ins (D14): each in one slot, and no object besides (a
                // silhouette is a stand-in too, its item's object elsewhere).
                var standIns = InventoryStandIn.All.Where(s => s.Relation == ItemRelation.Mine && !InventorySilhouettes.IsSilhouette(s)).ToList();
                foreach (var s in standIns)
                {
                    int slot = inv.IndexOf(s.gameObject);
                    if (slot < 0) failures.Add($"{s.PrefabName} {s.NetId} is a stand-in in no slot (claim {s.ClaimSlot})");
                    if (mine.Any(n => n.NetId == s.NetId)) failures.Add($"{s.PrefabName} {s.NetId} is a stand-in and an object at once");
                }
                var names = mine.Select(n => (Name(n.gameObject), n.NetId)).Concat(standIns.Select(s => (s.PrefabName, s.NetId))).ToList();
                // DV's own essentials: one of each, owned or not, must exist for this player.
                foreach (string prefab in new[] { "CommsRadio", "Map", "MapSchematic", "RouteMap", "wallet", "VehicleCatalog", "Compass" })
                {
                    int count = names.Count(n => n.Item1 == prefab);
                    if (count != 1) failures.Add($"{prefab}: {count} owned, expected 1");
                }
                return new Check
                {
                    Layout = string.Join(" ", layout), Failures = failures.ToArray(),
                    RadioId = names.FirstOrDefault(n => n.Item1 == "CommsRadio").NetId
                };
            }

            /// <summary>Cleanup: the essential back in its own slot (whatever is there moves
            /// to the last free slot first).</summary>
            [Step]
            public static IEnumerator PutBack(ushort id, int slot)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                int now = PlayerInventory.Slot(id);
                if (now == slot || now < 0) yield break;
                if (inv.PeekItemAtSlot(slot, true) != null) inv.MoveItemFromTo(slot, inv.GetLastFreeSlot(), false);
                inv.MoveItemFromTo(now, slot, false);
                yield return new WaitForSeconds(2);
            }

            /// <summary>The item in this player's slot, its NetId (0: none).</summary>
            [Step]
            public static ushort At(int slot) => PlayerInventory.NetIdOf(DV.InventorySystem.Inventory.Instance.PeekItemAtSlot(slot, false));
        }
    }
}
