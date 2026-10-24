using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The owned-items ledger is saved in Multiplayer.Items and comes back, through two restarts
    /// (docs/design/items/save-load.md). The client owns items in Lost and Found, in its inventory,
    /// in its hand, lying in the world and in a loco's cab; on a player host the host's player owns
    /// two more. The ledger must list each, live, with its owner and where it is, agree with the
    /// records, and keep all of it through a save and restart (the hand item comes back in the
    /// inventory). A second save and restart must give the same entries, born as they were
    /// (OI-PER-04). Last, a clone of an owned item made with its identity copied gets an id of its
    /// own, and the original keeps its id and place.
    /// </summary>
    [Scenario("ledger-save", "The owned-items ledger survives two saves and restarts in Multiplayer.Items, and a cloned item gets an id of its own")]
    [Needs(Need.RestartsGames)]
    [Budget(2100)]
    public sealed class LedgerSave : Scenario<LedgerSave.Options>
    {
        public sealed class Options
        {
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
        }

        private sealed class Owned_
        {
            public string What, Id, Owner, Where;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            string host = await pair.Host.Run(() => Owned.HostIdentity());
            List<Owned_> items = new();
            // The items go at the end, by id (each restart gives them new net IDs), so the next
            // scenario doesn't find an extra flashlight in an inventory.
            pair.Cleanup(async () =>
            {
                var now = new List<ushort>();
                foreach (Owned_ item in items)
                {
                    ushort netId = await pair.Host.Run(() => Owned.NetIdOf(item.Id));
                    if (netId != 0) now.Add(netId);
                }
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, now.ToArray()), 60);
            });

            async Task<string> Own(string what, ushort netId, string owner, string where)
            {
                string id = await pair.Host.Run(() => Owned.IdOf(netId));
                Require.That(id.Length > 0, $"the {what} has an id", $"netId {netId}");
                items.Add(new Owned_ { What = what, Id = id, Owner = owner, Where = where });
                return id;
            }

            // Lost and Found first: the walk away would collect the others too.
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 50, Nearby = 10, Grace = 1 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            ushort lost = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(lost), 60);
            await pair.Client.Run<Abs>(s => Walk.Away(s, 300, 0));
            if (!pair.Dedicated) await pair.Host.Run<Abs>(s => Walk.Away(s, 300, 20));
            await Until.Value(() => pair.Host.Run(() => Items.Record(lost)), r => r.Lost, 30, "the host collects the mug into Lost and Found");
            await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old));
            await Own("mug in Lost and Found", lost, client, "LostAndFound");

            ushort stashed = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Flashlight", "client"));
            await pair.Client.Run<int>(s => Items.Stash(s, stashed, false), 60);
            await Own("flashlight in the client's inventory", stashed, client, "Slot " + client);

            ushort dropped = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(dropped), 60);
            await Own("mug on the ground", dropped, client, "World");

            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));
            ushort cab = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(cab), 60);   // the client's before it boards with it
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, cab, "drop", 90), 90);
            string cabId = await Own("mug in the loco's cab", cab, client, "TrainInterior");

            ushort held = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Lantern", "client"));
            await pair.Client.Run<int>(s => Items.Stash(s, held, true), 60);
            await Own("lantern in the client's hand", held, client, "Hand " + client);

            if (!pair.Dedicated)
            {
                ushort hostStashed = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Flashlight", "host"));
                await pair.Host.Run<int>(s => Items.Stash(s, hostStashed, false), 60);
                await Own("flashlight in the host's inventory", hostStashed, host, "Slot " + host);
                ushort hostDropped = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "host"));
                await pair.Host.Run(() => LostAndFound.InGame.Claim(hostDropped), 60);
                await Own("the host's mug on the ground", hostDropped, host, "World");
            }
            // Back beside the mug on the ground, so the client comes back from the restart next to it:
            // an owner more than 200 m from their item gets it collected into Lost and Found.
            await pair.Client.Run(() => Owned.GoTo(dropped), 60);
            await Task.Delay(2000);   // the last moves reach the ledger
            await CheckAll(pair, items, "before the save", null);

            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart(o.Session);
            await Task.Delay(15000);   // the client receives its restored items
            Expect.That(await pair.Host.Run(() => Owned.Loaded()), "the ledger was loaded after the restart");
            // A hand item is saved in the inventory.
            foreach (Owned_ item in items.Where(i => i.Where.StartsWith("Hand ")))
                item.Where = "Slot" + item.Where.Substring("Hand".Length);
            await CheckAll(pair, items, "after the restart", null);

            string[] borns = (await pair.Host.Run(() => Owned.Entries(items.Select(i => i.Id).ToArray()))).Select(e => e.Born).ToArray();
            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart(o.Session);
            await Task.Delay(15000);
            await CheckAll(pair, items, "after a second restart", null);
            LedgerEntry[] again = await pair.Host.Run(() => Owned.Entries(items.Select(i => i.Id).ToArray()));
            for (int i = 0; i < items.Count; i++)
                Expect.Equal(borns[i], again[i].Born, $"{items[i].What}: how its entry was born, after a second restart");

            ushort original = await pair.Host.Run(() => Owned.NetIdOf(cabId));
            if (!Expect.That(original != 0, "the cab mug is there to clone", cabId))
                return;
            LedgerEntry before = await pair.Host.Run(() => Owned.Entry(cabId));
            ushort clone = await pair.Host.Run<ushort>(s => Owned.Clone(s, original));
            string cloneId = await pair.Host.Run(() => Owned.IdOf(clone));
            Report.Note($"cloned {original} as {clone}: id {cloneId}");
            if (cloneId.Length > 0) items.Add(new Owned_ { What = "the cab mug's clone", Id = cloneId });
            Expect.That(cloneId.Length > 0 && cloneId != cabId, "the clone has an id of its own", cloneId);
            Expect.Equal(original, await pair.Host.Run(() => Owned.NetIdOf(cabId)), "the original still has its id");
            LedgerEntry after = await pair.Host.Run(() => Owned.Entry(cabId));
            Expect.Equal(before.Where, after.Where, "the original's place in the ledger");
        }

        private static async Task CheckAll(Pair pair, List<Owned_> items, string when, string born)
        {
            LedgerEntry[] entries = await pair.Host.Run(() => Owned.Entries(items.Select(i => i.Id).ToArray()));
            for (int i = 0; i < items.Count; i++)
            {
                Owned_ want = items[i];
                LedgerEntry got = entries[i];
                Report.Note($"{want.What} {when}: {got}");
                if (!Expect.That(got.Known && got.Live, $"{want.What}: live in the ledger {when}", got.ToString()))
                    continue;
                Expect.Equal(want.Owner, got.Owner, $"{want.What}: its owner {when}");
                Expect.That(got.Where.StartsWith(want.Where), $"{want.What}: where it is {when}", $"{got.Where}, wanted {want.Where}");
                if (born != null)
                    Expect.Equal(born, got.Born, $"{want.What}: how its entry was born {when}");
            }
            Expect.Empty(await SettledAudit(pair, items.Select(i => i.Id).ToArray()), $"what the ledger disagrees with {when}");
        }

        /// <summary>
        /// The audit for these ids, read until it is empty or 30 s pass. A joining client's Lost and
        /// Found entries bind on a later server tick, and until then its inert items' records still
        /// say World (on a dedicated host the client joins well after the host has loaded).
        /// </summary>
        internal static async Task<string[]> SettledAudit(Pair pair, string[] ids)
        {
            string[] audit = System.Array.Empty<string>();
            for (int i = 0; i < 30; i++)
            {
                audit = await pair.Host.Run<string[]>(s => Owned.AuditFor(s, ids));
                if (audit.Length == 0) break;
                await Task.Delay(1000);
            }
            return audit;
        }
    }
}
