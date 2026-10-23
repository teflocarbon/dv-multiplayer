using System;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Shared.Items.LiveState;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Cold and live homes (docs/design/items/inventory.md, "Cold inventories" and "Data only on the
    /// server"; OI-COLD-01, -02, -04, -05, -06). An item in a connected client's inventory is data on
    /// the server, with no object there, its ledger home its slot. A move to another slot has the
    /// object made again, moves it and lets it go back to data. A move the server refuses (a stale
    /// revision) puts it back where it was in the client's game. When the client leaves, the item
    /// is a cold row in the same slot, with no object anywhere and its ledger home unchanged; at the
    /// rejoin it is made again in that slot.
    /// </summary>
    [Scenario("cold-homes", "Inventory items are data on the server: no duplicate, moves made live, a refused move rolls back, cold rows across a rejoin")]
    [Needs(Need.RestartsGames)]
    [Budget(300)]
    public sealed class ColdHomes : Scenario<ColdHomes.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort lantern = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Lantern", "client"));
            int slot = await pair.Client.Run<int>(s => Items.Stash(s, lantern, false), 60);
            string id = await pair.Host.Run(() => Owned.IdOf(lantern));
            Require.That(id.Length > 0, "the lantern has an id", lantern.ToString());

            // Data only on the server: its record and row, no object.
            await Until.Value(() => pair.Host.Run(() => InGame.State(id)), state => state == "data", 20,
                "the server keeps the client's lantern as data");
            await Same(pair, lantern, slot, "the lantern, data on the server");
            Expect.Equal(slot, await pair.Host.Run(() => InGame.RowSlot(id)), "the lantern's row: its slot");

            // A move: the server makes the object again for the request, moves it, then lets it go.
            int free = await pair.Client.Run(() => InventorySlots.InGame.FreeSlot(slot, slot));
            Expect.That(await pair.Client.Run<bool>(s => InventorySlots.InGame.Move(s, lantern, free)),
                "the client's game moved the lantern", $"to slot {free}");
            await Same(pair, lantern, free, "the lantern, moved while data");
            await Until.Value(() => pair.Host.Run(() => InGame.State(id)), state => state == "data", 20,
                "the lantern is data again after the move");
            Expect.Equal(free, await pair.Host.Run(() => InGame.RowSlot(id)), "the lantern's row, after the move");

            // A refused move rolls back: the server's revision moves on, so the client's request is stale.
            uint revision = await pair.Host.Run(() => InGame.BumpRevision(lantern));
            Report.Note($"the server's revision is now {revision}");
            int other = await pair.Client.Run(() => InventorySlots.InGame.FreeSlot(free, slot));
            Expect.That(await pair.Client.Run<bool>(s => InventorySlots.InGame.Move(s, lantern, other)),
                "the client's game moved the lantern before the server answered", $"to slot {other}");
            await Same(pair, lantern, free, "the lantern, its move refused");

            // Cold: the client leaves; the lantern is a row in its slot, no object anywhere.
            await Until.Value(() => pair.Host.Run(() => InGame.State(id)), state => state == "data", 20,
                "the lantern is data again before the client leaves");
            await pair.Client.Quit();
            await Until.Value(() => pair.Host.Run(() => InGame.State(id)), state => state == "cold", 30,
                "the lantern is cold once the client has left");
            Expect.Equal(free, await pair.Host.Run(() => InGame.RowSlot(id)), "the cold row's slot");
            Expect.Equal(free, await pair.Host.Run(() => InGame.LedgerSlot(id)), "the ledger's slot for the cold lantern");

            // Live again: the rejoin makes it in the same slot.
            await pair.Rejoin();
            await Task.Delay(12000);   // the client receives its items
            ushort now = await pair.Host.Run(() => Owned.NetIdOf(id));
            Require.That(now != 0, "the lantern is live again after the rejoin");
            await pair.Host.Run(() => Ledger.Track(now));
            await Same(pair, now, free, "the lantern, after the rejoin");
        }

        // The server's record and the ledger have it in this slot, and so does the client's game.
        private static async Task Same(Pair pair, ushort netId, int slot, string what)
        {
            ServerSlot server = null;
            int local = -1;
            for (int i = 0; i < 12; i++)
            {
                server = await pair.Host.Run(() => Ledger.Where(netId));
                local = await pair.Client.Run(() => Ledger.LocalSlot(netId));
                if (server.Slot == slot && server.LedgerSlot == slot && local == slot) break;
                await Task.Delay(500);
            }
            Report.Note($"{what}: server {server}, client slot {local}");
            Expect.Equal(slot, server.Slot, $"{what}: the server's slot");
            Expect.Equal(slot, server.LedgerSlot, $"{what}: the ledger's slot");
            Expect.Equal(slot, local, $"{what}: the client's slot");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: "object" (the server has one), "data" (a live record and a row, no object),
            /// "cold" (a row only), or "gone".</summary>
            [Step]
            public static string State(string id)
            {
                Guid wanted = Guid.Parse(id);
                LiveItems.LiveItem record = LiveItems.GetAll().FirstOrDefault(r => r.PersistentItemId == wanted);
                bool row = ColdInventories.Contains(wanted);
                if (record != null && NetworkedItem.TryGet(record.NetId, out NetworkedItem item) && item != null)
                    return row ? "object and row" : "object";
                if (record != null)
                    return row && PlayerItems.IsDataOnly(record) ? "data" : "record only";
                return row ? "cold" : "gone";
            }

            /// <summary>Host: the slot of the item's cold-inventory row, or -2 if it has none.</summary>
            [Step]
            public static int RowSlot(string id) =>
                ColdInventories.TryGet(Guid.Parse(id), out var row) ? row.Slot : -2;

            /// <summary>Host: the ledger's slot for the item, or -1 if its home isn't a slot.</summary>
            [Step]
            public static int LedgerSlot(string id) =>
                ServerLedger.Ledger.TryGetHome(Guid.Parse(id), out Home home) && home.Kind == HomeKind.Slot ? home.Slot : -1;

            /// <summary>Host: moves the item's revision on, as a change the client hasn't heard of yet would.</summary>
            [Step]
            public static uint BumpRevision(ushort netId)
            {
                if (!LiveItems.TryGet(netId, out LiveItems.LiveItem record)) throw new Exception($"no record for {netId}");
                record.Revision += 3;
                return record.Revision;
            }
        }
    }
}
