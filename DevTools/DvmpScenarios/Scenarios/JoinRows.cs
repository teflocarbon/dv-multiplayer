using System;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A joiner draws their inventory from rows (docs/design/items/inventory.md, "Stand-ins"; D14,
    /// OI-COLD-09): the client stashes a few items, quits and joins again, and every one is a
    /// stand-in in the slot it left in, with no object made for it in the new game (no Create, no
    /// make from its row, no take-out) and the server and ledger agreeing. WHO=host does it with the
    /// host's own player (R4C C4): it stashes, saves, and both games restart from the save (a player
    /// host only). The items are destroyed at the end.
    /// </summary>
    [Scenario("join-rows", "A rejoined client's inventory is stand-ins from its rows in their slots, with no object made for any of them")]
    [Needs(Need.RestartsGames)]
    [Budget(300)]
    public sealed class JoinRows : Scenario<JoinRows.Options>
    {
        public sealed class Options
        {
            /// <summary>The items to stash, comma-separated.</summary>
            public string Prefabs = "Lantern,Mug,lighter";
            /// <summary>Whose inventory: "client", or "host" (the host's own player; needs a player host).</summary>
            public string Who = "client";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            bool host = o.Who == "host";
            if (host && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            Peer who = host ? pair.Host : pair.Client;
            string[] prefabs = o.Prefabs.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            var ids = new string[prefabs.Length];
            var slots = new int[prefabs.Length];
            for (int i = 0; i < prefabs.Length; i++)
            {
                string prefab = prefabs[i];
                ushort netId = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, prefab, o.Who));
                slots[i] = await who.Run<int>(s => Items.Stash(s, netId, false), 60);
                ids[i] = await pair.Host.Run(() => Owned.IdOf(netId));
                Report.Note($"{prefab} {netId} ({ids[i]}) in slot {slots[i]}");
            }

            if (host)
            {
                await pair.Host.Run(() => Saves.Manual());
                await pair.Restart();
                await Task.Delay(12000);   // the host's rows are drawn
            }
            else
            {
                await pair.Client.Quit();
                await Task.Delay(4000);
                await pair.Rejoin();
                await Task.Delay(12000);   // the client receives its rows
            }

            ushort[] now = new ushort[prefabs.Length];
            for (int i = 0; i < prefabs.Length; i++)
            {
                string id = ids[i];
                now[i] = await pair.Host.Run(() => Owned.NetIdOf(id));
                ushort netId = now[i];
                await pair.Host.Run(() => Ledger.Track(netId));
            }
            pair.Cleanup(async () => await pair.Host.Run<ushort[]>(s => Items.Destroy(s, now.Where(id => id != 0).ToArray()), 30));
            Seen seen = await who.Run(() => InGame.Look(now));
            Report.Note($"the {who.Name} after the rejoin: {seen}");
            if (!seen.Tracing) Report.Note($"the {who.Name}'s debug events are off: the made-objects count is unchecked");
            for (int i = 0; i < prefabs.Length; i++)
            {
                ushort netId = now[i];
                string what = $"the {prefabs[i]} after the rejoin";
                Expect.That(netId != 0, $"{what}: the server still has it");
                Expect.Equal(slots[i], seen.Slots[i], $"{what}: the {who.Name}'s slot");
                Expect.That(seen.StandIns[i], $"{what}: a stand-in on the {who.Name}");
                Expect.That(!seen.Objects[i], $"{what}: no object on the {who.Name}");
                ServerSlot server = await pair.Host.Run(() => Ledger.Where(netId));
                Expect.Equal(slots[i], server.Slot, $"{what}: the server's slot");
                Expect.Equal(slots[i], server.LedgerSlot, $"{what}: the ledger's slot");
            }
            Expect.Empty(seen.Made, $"objects the {who.Name} made for its stored items since it started");
        }

        public sealed class Seen
        {
            public int[] Slots = new int[0];
            public bool[] StandIns = new bool[0], Objects = new bool[0];
            public string[] Made = new string[0];
            public bool Tracing;

            public override string ToString() =>
                string.Join(", ", Slots.Select((s, i) => $"slot {s}{(StandIns[i] ? " stand-in" : "")}{(Objects[i] ? " object" : "")}")) +
                (Made.Length > 0 ? "; made: " + string.Join(", ", Made) : "");
        }

        // ---- runs inside the games
        public static class InGame
        {
            // What makes an object: a Create, a make from a row, a take-out, any object taking the NetId.
            private static readonly string[] Makes = { "item.instantiated", "item.created", "item.made-from-row", "item.taken-out-of-stand-in", "item.registered" };

            /// <summary>The holder's game: where each item is, whether it's a stand-in or an object, and any
            /// object made for one since this game started (its debug events).</summary>
            [Step]
            public static Seen Look(ushort[] ids)
            {
                var names = ids.Select(id => id.ToString()).ToArray();
                var store = DebugRuntime.Store;
                return new Seen
                {
                    Slots = ids.Select(PlayerInventory.Slot).ToArray(),
                    StandIns = ids.Select(id => InventoryStandIn.TryGet(id, out _)).ToArray(),
                    Objects = ids.Select(id => NetworkedItem.TryGet(id, out NetworkedItem n) && n != null).ToArray(),
                    Tracing = store != null && DebugRuntime.EnabledFor("item"),
                    Made = store == null ? new string[0] : store.Snapshot()
                        .Where(e => Makes.Contains(e.EventName) && names.Contains(e.EntityId))
                        .Select(e => $"{e.EventName} {e.EntityId}").ToArray()
                };
            }
        }
    }
}
