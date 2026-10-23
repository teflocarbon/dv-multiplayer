using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The one-home repair a Release build makes (D7, OI-LED-15; docs/design/items/ledger.md): an item
    /// whose ledger home isn't where it is goes to its ledger home, else to its owner's Lost and Found.
    /// A Debug build only reports a breach, so the run breaks the ledger on purpose and calls the
    /// repair (<c>ServerLedger.PutHome</c>) itself:
    /// - lying in the world, home a connected player's slot: into that slot, on both games;
    /// - in the client's inventory, home Lost and Found: collected there;
    /// - in Lost and Found, home the client's slot: recalled into it;
    /// - in the client's inventory, home the ground (no pose kept): the owner's Lost and Found;
    /// - lying in the world, home an offline player's slot: a cold row there, its object gone;
    /// - on a player host, lying in the world, home the host's own player's slot: into it; and from
    ///   there, home the client's slot: out of the host's inventory into the client's.
    /// The one-home check holds after each.
    /// </summary>
    [Scenario("ledger-repair", "The one-home repair (D7): an item outside its ledger home is put there, else in its owner's Lost and Found")]
    [Budget(240)]
    public sealed class LedgerRepair : Scenario<LedgerRepair.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(mug, "client"));
            string mugId = await pair.Host.Run(() => Owned.IdOf(mug));
            await Task.Delay(2000);   // settled on the ground

            // In the world, home the client's slot: put there.
            int slot = await pair.Host.Run(() => InGame.FreeSlot(client, mugId));
            await Repaired(pair, mugId, "slot", client, slot, $"slot {slot} of {client}", "the mug on the ground, home the client's slot");
            await InSlot(pair, pair.Client, mug, slot, "the mug put in the client's slot");

            // In the client's inventory, home Lost and Found: collected.
            await Task.Delay(3000);   // data only on the server: the repair makes it again first
            await Repaired(pair, mugId, "lost", "", -1, "LostAndFound", "the mug in the client's inventory, home Lost and Found");
            Expect.Equal("SaveRecovery", await pair.Host.Run(() => LedgerRows.InGame.LostReason(client, mugId)), "why Lost and Found has it");

            // In Lost and Found, home the client's slot: recalled.
            mug = await pair.Host.Run(() => Owned.NetIdOf(mugId));
            slot = await pair.Host.Run(() => InGame.FreeSlot(client, mugId));
            await Repaired(pair, mugId, "slot", client, slot, $"slot {slot} of {client}", "the mug in Lost and Found, home the client's slot");
            mug = await pair.Host.Run(() => Owned.NetIdOf(mugId));
            await pair.Host.Run(() => Ledger.Track(mug));
            await InSlot(pair, pair.Client, mug, slot, "the mug recalled into the client's slot");

            // In the client's inventory, home the ground: the ledger keeps no pose, so Lost and Found.
            await Task.Delay(3000);
            await Repaired(pair, mugId, "world", "", -1, "LostAndFound", "the mug in the client's inventory, home the ground");

            // Lying in the world, home an offline player's slot: a cold row.
            ushort cup = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(cup, "client"));
            string cupId = await pair.Host.Run(() => Owned.IdOf(cup));
            string absent = Guid.NewGuid().ToString("D");
            pair.Cleanup(() => pair.Host.Run(() => InGame.Forget(cupId)));
            await Task.Delay(2000);
            await Repaired(pair, cupId, "slot", absent, 4, $"slot 4 of {absent}", "the cup on the ground, home an offline player's slot 4");
            Expect.Equal($"{absent} 4", await pair.Host.Run(() => InGame.ColdRow(cupId)), "the cup's cold row");
            Expect.That(!(await pair.Host.Run(() => Ownership.ById(cupId))).Exists, "the cup has no object on the server");

            if (pair.Dedicated)
            {
                Report.Note("a dedicated host has no player of its own: the host's slot part needs one");
                return;
            }

            // On a player host, the host's own player's slot.
            string host = await pair.Host.Run(() => InGame.HostIdentity());
            ushort lent = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "host"));
            await pair.Host.Run(() => Ownership.Give(lent, "client"));
            string lentId = await pair.Host.Run(() => Owned.IdOf(lent));
            await Task.Delay(2000);
            slot = await pair.Host.Run(() => InGame.FreeSlot(host, lentId));
            await Repaired(pair, lentId, "slot", host, slot, $"slot {slot} of {host}", "the client's mug on the ground, home the host's slot");
            await InSlot(pair, pair.Host, lent, slot, "the mug put in the host's slot");

            // In the host's inventory, home the client's slot: out of the host's hands, into the client's.
            slot = await pair.Host.Run(() => InGame.FreeSlot(client, lentId));
            await Repaired(pair, lentId, "slot", client, slot, $"slot {slot} of {client}", "the mug in the host's inventory, home the client's slot");
            await InSlot(pair, pair.Client, lent, slot, "the mug moved from the host's slot to the client's");
            Expect.Equal(-1, await pair.Host.Run(() => Ledger.LocalSlot(lent)), "the host's game no longer has it in its inventory");
        }

        // The ledger is set to the home, the repair runs, and the ledger ends where expected with the
        // one-home check holding.
        private static async Task Repaired(Pair pair, string id, string kind, string holder, int slot, string expected, string what)
        {
            string before = (await pair.Host.Run(() => Ownership.ById(id))).ToString();
            string home = await pair.Host.Run<string>(s => InGame.Repair(s, id, kind, holder, slot), 30);
            Report.Note($"{what}: was {before}; the ledger now {home}");
            Expect.Equal(expected, home, $"{what}: the ledger home after the repair");
            string[] broken = (await pair.Host.Run(() => Invariant.Check())).Where(l => l.Contains(id)).ToArray();
            Expect.Empty(broken, $"{what}: the one-home check after the repair");
        }

        private static async Task InSlot(Pair pair, Peer who, ushort netId, int slot, string what)
        {
            ServerSlot server = null;
            int local = -1;
            for (int i = 0; i < 20; i++)
            {
                server = await pair.Host.Run(() => Ledger.Where(netId));
                local = await who.Run(() => Ledger.LocalSlot(netId));
                if (server.Slot == slot && local == slot) break;
                await Task.Delay(500);
            }
            Report.Note($"{what}: server {server}, {who.Name} slot {local}");
            Expect.Equal(slot, server.Slot, $"{what}: the server's slot");
            Expect.Equal(slot, local, $"{what}: the {who.Name}'s slot");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the host's own player's identity.</summary>
            [Step]
            public static string HostIdentity() => NetworkServer.Instance.LocalPlayer.Guid.ToString("D");

            /// <summary>Host: a slot the ledger would give the item in the holder's inventory, off the belt.</summary>
            [Step]
            public static int FreeSlot(string holder, string id)
            {
                Guid item = Guid.Parse(id), who = Guid.Parse(holder);
                for (int s = Multiplayer.Core.Items.Inventory.InventorySlots.FirstBeltSlot - 1; s >= 0; s--)
                    if (ServerLedger.Ledger.CanTake(Home.InSlot(who, s), item, ServerLedger.Ledger.OwnerOf(item)).Length == 0 &&
                        ColdInventories.At(who, s) == null)
                        return s;
                throw new Exception("no free slot off the belt");
            }

            /// <summary>
            /// Host: breaks the one-home rule on purpose (the ledger says <paramref name="kind"/>: "slot"
            /// of the holder, "lost" or "world"), then repairs it as a Release build does. Result: the
            /// ledger's home once the repair has run.
            /// </summary>
            [Step]
            public static IEnumerator Repair(Step<string> step, string id, string kind, string holder, int slot)
            {
                Guid item = Guid.Parse(id);
                Home want = kind switch
                {
                    "slot" => Home.InSlot(Guid.Parse(holder), slot),
                    "lost" => Home.LostAndFound,
                    "world" => Home.World,
                    _ => throw new ArgumentException(kind)
                };
                LiveItems.LiveItem record = LiveItems.GetAll().First(r => r.PersistentItemId == item);
                ServerLedger.Ledger.Repair(item, want, new LedgerCause(ItemMoveReason.HostCorrection, Guid.Empty, "ledger-repair"));
                ServerLedger.PutHome(record);
                float t0 = UnityEngine.Time.realtimeSinceStartup;
                while (UnityEngine.Time.realtimeSinceStartup - t0 < 10f &&
                       (ServerLedger.CheckNow().Any(l => l.Contains(id)) || !Settled(item, want)))
                    yield return null;
                for (int i = 0; i < 30; i++) yield return null;
                step.Result = ServerLedger.Ledger.TryGetHome(item, out Home home) ? home.ToString() : "";
            }

            private static bool Settled(Guid item, Home want) =>
                ServerLedger.Ledger.TryGetHome(item, out Home home) && (home == want || home.Kind == HomeKind.LostAndFound);

            /// <summary>Host: the item's cold row, "holder slot", or "".</summary>
            [Step]
            public static string ColdRow(string id) =>
                ColdInventories.TryGet(Guid.Parse(id), out var row) ? $"{row.PlacementPlayerIdentity:D} {row.Slot}" : "";

            /// <summary>Host: an offline player's item made up for the run goes: its cold row and its entry.</summary>
            [Step]
            public static void Forget(string id)
            {
                Guid item = Guid.Parse(id);
                ColdInventories.Remove(item);
                ServerLedger.Ledger.End(item, ItemRemovalReason.DestroyedByGame, detail: "ledger-repair");
            }
        }
    }
}
