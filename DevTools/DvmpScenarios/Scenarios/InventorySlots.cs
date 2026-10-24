using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The server keeps every inventory slot (docs/design/items/inventory.md; OI-SLOT-01..03): a
    /// stash lands in the slot the player put it in, a move to another slot and a swap of two items
    /// reach the server and the ledger, an essential moved off the belt claims its new slot and one
    /// on the belt keeps its old claim, a move into a slot another essential reserves is refused and
    /// leaves the item where it was in both games, and after a rejoin every item is back in the same
    /// slot. WHO=host does it with the host's own player. RESTART=1 saves and restarts the pair
    /// instead of the rejoin (docs/design/items/save-load.md), which is how WHO=host comes back.
    /// </summary>
    [Scenario("inventory-slots", "Inventory slots are the server's: stash, move, swap, the belt, a reserved slot, and a rejoin into the same slots")]
    [Needs(Need.RestartsGames)]
    [Budget(500)]
    public sealed class InventorySlots : Scenario<InventorySlots.Options>
    {
        public sealed class Options
        {
            /// <summary>The essential to move about (the comms radio).</summary>
            public string Essential = "CommsRadio";
            /// <summary>Whose inventory: "client", or "host" (the host's own player; needs a player host).</summary>
            public string Who = "client";
            /// <summary>True: save and restart the pair instead of a rejoin.</summary>
            public bool Restart = false;
            /// <summary>The save to reload (RESTART=1).</summary>
            public string Session = "DVMP Test Career 2";
        }

        // The belt's first slot (InventorySlots.FirstBeltSlot): game code, so not callable from Run.
        private const int FirstBeltSlot = 33;

        protected override async Task Run(Pair pair, Options o)
        {
            bool host = o.Who == "host";
            if (host && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            Peer who = host ? pair.Host : pair.Client;
            ushort first = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Lantern", o.Who));
            ushort second = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Lantern", o.Who));
            int firstSlot = await who.Run<int>(s => Items.Stash(s, first, false), 60);
            int secondSlot = await who.Run<int>(s => Items.Stash(s, second, false), 60);
            await Same(pair, who, first, firstSlot, "the first lantern, stashed");
            await Same(pair, who, second, secondSlot, "the second lantern, stashed");

            // A move to a free slot.
            int free = await who.Run(() => InGame.FreeSlot(firstSlot, secondSlot));
            Expect.That(await who.Run<bool>(s => InGame.Move(s, first, free)), "the game moved the first lantern", $"to slot {free}");
            await Same(pair, who, first, free, "the first lantern, moved");

            // A swap: one request, both move.
            Expect.That(await who.Run<bool>(s => InGame.Swap(s, first, second)), "the game swapped the lanterns");
            await Same(pair, who, first, secondSlot, "the first lantern, swapped");
            await Same(pair, who, second, free, "the second lantern, swapped");

            // An essential off the belt claims its new slot; on the belt it keeps its claim.
            ushort radio = await who.Run(() => InGame.Find(o.Essential));
            if (!Expect.That(radio != 0, $"the player has its {o.Essential}")) return;
            ServerSlot home = await pair.Host.Run(() => Ledger.Where(radio));
            Report.Note($"{o.Essential}: {home}");
            int claim = home.Claim;
            string radioId = await pair.Host.Run(() => Owned.IdOf(radio));
            pair.Cleanup(async () =>
            {
                ushort now = await pair.Host.Run(() => Owned.NetIdOf(radioId));
                if (await who.Run(() => Ledger.LocalSlot(now)) < 0)
                    await who.Run<int>(s => Items.Stash(s, now, false), 60);
                await who.Run<bool>(s => InGame.Move(s, now, claim));
            });
            int elsewhere = await who.Run(() => InGame.FreeSlot(claim, claim));
            Expect.That(await who.Run<bool>(s => InGame.Move(s, radio, elsewhere)), $"the game moved the {o.Essential}", $"to slot {elsewhere}");
            ServerSlot moved = await Same(pair, who, radio, elsewhere, $"the {o.Essential}, moved off its claim");
            Expect.Equal(elsewhere, moved.Claim, $"the {o.Essential}'s claim, after its owner put it in slot {elsewhere}");
            Expect.That(await who.Run<bool>(s => InGame.Move(s, radio, FirstBeltSlot)), $"the game put the {o.Essential} on the belt");
            ServerSlot belt = await Same(pair, who, radio, FirstBeltSlot, $"the {o.Essential}, on the belt");
            Expect.Equal(elsewhere, belt.Claim, $"the {o.Essential}'s claim, while it's on the belt");

            Expect.That(await who.Run<bool>(s => InGame.Move(s, radio, claim)), $"the game put the {o.Essential} back");
            ServerSlot back = await Same(pair, who, radio, claim, $"the {o.Essential}, back in its first slot");
            Expect.Equal(claim, back.Claim, $"the {o.Essential}'s claim, back in its first slot");

            // Out of the inventory, its claim reserves the slot: a move into it is refused, and the
            // item stays where it was in both games.
            await who.Run(() => InGame.Drop(radio));
            Expect.That(await who.Run<bool>(s => InGame.Request(s, first, claim)), "the player asked to move the first lantern into the reserved slot");
            await Same(pair, who, first, secondSlot, "the first lantern, refused the reserved slot");
            await who.Run<int>(s => Items.Stash(s, radio, false), 60);
            await Same(pair, who, radio, claim, $"the {o.Essential}, picked up again");
            if (host && !o.Restart)
                return;

            // A rejoin, or a save and restart: every item comes back in the slot it left in (its
            // net ID may be new).
            string firstId = await pair.Host.Run(() => Owned.IdOf(first));
            string secondId = await pair.Host.Run(() => Owned.IdOf(second));
            string again = o.Restart ? "after the restart" : "after the rejoin";
            if (o.Restart)
            {
                await pair.Host.Run(() => Saves.Manual());
                await pair.Restart(o.Session);
                await Task.Delay(15000);   // the client receives its items
            }
            else
            {
                await pair.Client.Quit();
                await Task.Delay(4000);
                await pair.Rejoin();
                await Task.Delay(12000);   // the client receives its items
            }
            ushort firstNow = await pair.Host.Run(() => Owned.NetIdOf(firstId));
            ushort secondNow = await pair.Host.Run(() => Owned.NetIdOf(secondId));
            ushort radioNow = await pair.Host.Run(() => Owned.NetIdOf(radioId));
            await pair.Host.Run(() => Ledger.Track(firstNow));
            await pair.Host.Run(() => Ledger.Track(secondNow));
            await Same(pair, who, firstNow, secondSlot, "the first lantern, " + again);
            await Same(pair, who, secondNow, free, "the second lantern, " + again);
            await Same(pair, who, radioNow, claim, $"the {o.Essential}, " + again);
        }

        // The server has it in this slot, the ledger agrees, and so does the player's game.
        private static async Task<ServerSlot> Same(Pair pair, Peer who, ushort netId, int slot, string what)
        {
            ServerSlot server = null;
            int local = -1;
            for (int i = 0; i < 10; i++)
            {
                server = await pair.Host.Run(() => Ledger.Where(netId));
                local = await who.Run(() => Ledger.LocalSlot(netId));
                if (server.Slot == slot && server.LedgerSlot == slot && local == slot) break;
                await Task.Delay(500);
            }
            Report.Note($"{what}: server {server}, {who.Name} slot {local}");
            Expect.Equal(slot, server.Slot, $"{what}: the server's slot");
            Expect.Equal(slot, server.LedgerSlot, $"{what}: the ledger's slot");
            Expect.Equal(slot, local, $"{what}: the {who.Name}'s slot");
            return server;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Client: the last free slot off the belt, other than these two.</summary>
            [Step]
            public static int FreeSlot(int a, int b)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                for (int s = Multiplayer.Core.Items.Inventory.InventorySlots.FirstBeltSlot - 1; s >= 0; s--)
                    if (s != a && s != b && inv.PeekItemAtSlot(s, true) == null && !inv.GetSlotReservedState(s)) return s;
                throw new Exception("no free slot off the belt");
            }

            /// <summary>Client: the net ID of the player's own item of this prefab in the inventory, or 0.</summary>
            [Step]
            public static ushort Find(string prefab)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                for (int s = 0; s < inv.Capacity; s++)
                {
                    GameObject go = inv.PeekItemAtSlot(s, false);
                    NetworkedItem item = go != null ? go.GetComponent<NetworkedItem>() : null;
                    if (item != null && item.NetId != 0 && go.name.Replace("(Clone)", "") == prefab) return item.NetId;
                }
                return 0;
            }

            /// <summary>Client: moves the item to <paramref name="slot"/> as dragging it there does. Result: whether the game moved it.</summary>
            [Step]
            public static IEnumerator Move(Step<bool> step, ushort netId, int slot)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                NetworkedItem.TryGet(netId, out NetworkedItem item);
                int from = item != null ? inv.IndexOf(item.gameObject) : -1;
                step.Result = from == slot || from >= 0 && inv.MoveItemFromTo(from, slot, false);
                yield return new WaitForSeconds(1.5f);
            }

            /// <summary>Client: drops the item from the inventory at the player's feet.</summary>
            [Step]
            public static IEnumerator Drop(ushort netId)
            {
                NetworkedItem.TryGet(netId, out NetworkedItem item);
                if (item == null || DV.InventorySystem.Inventory.Instance.DropItemFromHandsOrInventory(item.gameObject) == null)
                    throw new Exception($"couldn't drop item {netId}");
                yield return new WaitForSeconds(2f);
            }

            /// <summary>Client: asks the server to move the item to <paramref name="slot"/>
            /// without the game moving it first, as a game whose slot the server doesn't keep would.
            /// Result: whether the request went.</summary>
            [Step]
            public static IEnumerator Request(Step<bool> step, ushort netId, int slot)
            {
                NetworkedItem.TryGet(netId, out NetworkedItem item);
                step.Result = item != null && ItemClient.Current?.SendItemRequest(new ItemRequest
                {
                    ItemNetId = netId,
                    ExpectedRevision = item.AuthorityRevision,
                    Action = ItemAction.MoveSlot,
                    Slot = slot
                }) > 0;
                yield return new WaitForSeconds(1.5f);
            }

            /// <summary>Client: swaps two items' slots as dragging one onto the other does. Result: whether the game swapped them.</summary>
            [Step]
            public static IEnumerator Swap(Step<bool> step, ushort a, ushort b)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                NetworkedItem.TryGet(a, out NetworkedItem first);
                NetworkedItem.TryGet(b, out NetworkedItem second);
                int from = first != null ? inv.IndexOf(first.gameObject) : -1;
                int to = second != null ? inv.IndexOf(second.gameObject) : -1;
                step.Result = from >= 0 && to >= 0 && inv.SwapItems(from, to);
                yield return new WaitForSeconds(1.5f);
            }
        }
    }
}
