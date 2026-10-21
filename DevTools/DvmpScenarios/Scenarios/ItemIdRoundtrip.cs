using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// One id for an item's whole life (owned-items.md, PR 1b). The client owns a mug, puts it in its
    /// toolbox and takes it out again; the games save and restart; it leaves the mug behind and walks
    /// away, the host collects it into the client's Lost and Found; save and restart again; the client
    /// retrieves it. The mug must have the id it started with at every step, the Lost and Found entry
    /// must have that id too, and the ledger must follow it. The first restart is the known bug's
    /// check: an item withdrawn from a container used to get a record without its id, so it wasn't
    /// saved and was gone after the restart.
    /// </summary>
    [Scenario("item-id-roundtrip", "A mug keeps one id through a container, a restart, Lost and Found and another restart, and the ledger follows it")]
    [Needs(Need.RestartsGames)]
    [Budget(1500)]
    public sealed class ItemIdRoundtrip : Scenario<ItemIdRoundtrip.Options>
    {
        public sealed class Options
        {
            /// <summary>The item's prefab.</summary>
            public string Prefab = "Mug";
            /// <summary>The container's prefab.</summary>
            public string Container = "ItemContainerToolbox";
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Container, "client"));
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"));
            string id = await pair.Host.Run(() => Owned.IdOf(mug));
            Require.That(id.Length > 0, $"the {o.Prefab} has an id", $"netId {mug}");
            Report.Note($"{o.Prefab} {mug} has id {id}; the client is {client}");

            // Into the toolbox and out again.
            foreach (string line in await pair.Client.Run<string[]>(s => Containers.InGame.RoundTrip(s, box, mug), 120))
                Report.Note(line);
            ushort now = await pair.Host.Run(() => Owned.NetIdOf(id));
            Expect.That(now != 0, "after the withdrawal an item has the id it went in with", id);
            await Check(pair, id, client, "Inventory", "after the withdrawal");

            // The known bug: a withdrawn item wasn't saved.
            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart(o.Session);
            await Task.Delay(12000);   // the client receives its restored items
            now = await pair.Host.Run(() => Owned.NetIdOf(id));
            if (!Expect.That(now != 0, "after a restart the withdrawn item is still there, with its id", id))
                return;
            Items.Track(now);
            Expect.That(await pair.Client.Run(() => Owned.InInventory(now)), "the withdrawn item is back in the client's inventory", $"netId {now}");
            await Check(pair, id, client, "Inventory", "after the restart");

            // Into Lost and Found, and through another restart.
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 50, Nearby = 10, Grace = 1 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            await pair.Client.Run(() => Owned.DropFromInventory(now), 60);
            await pair.Client.Run<Abs>(s => Walk.Away(s, 300, 0));
            if (!pair.Dedicated) await pair.Host.Run<Abs>(s => Walk.Away(s, 300, 20));
            await Until.Value(() => pair.Host.Run(() => Items.Record(now)), r => r.Lost, 30, "the host collects the item into Lost and Found");
            Expect.Equal(id, await pair.Host.Run(() => Owned.LostEntryId(now)), "the Lost and Found entry's id (the item's own)");
            await Check(pair, id, client, "LostAndFound", "collected");
            await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old));

            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart(o.Session);
            await Task.Delay(12000);   // the client's Lost and Found entries bind when it joins
            now = await pair.Host.Run(() => Owned.NetIdOf(id));
            if (!Expect.That(now != 0, "after the second restart the item in Lost and Found has its id", id))
                return;
            Items.Track(now);
            Expect.Equal(id, await pair.Host.Run(() => Owned.LostEntryId(now)), "the Lost and Found entry's id after the restart");
            int slot = await pair.Client.Run<int>(s => LostAndFound.InGame.Retrieve(s, now), 60);
            Report.Note($"retrieved into slot {slot}");
            Expect.Equal(now, await pair.Host.Run(() => Owned.NetIdOf(id)), "the retrieved item's id");
            await Task.Delay(2000);   // the retrieval reaches the ledger
            await Check(pair, id, client, "Inventory", "after the retrieval");
        }

        // The ledger has the item live, the client's, where the step says, and agrees with the records.
        private static async Task Check(Pair pair, string id, string client, string where, string when)
        {
            LedgerEntry entry = await pair.Host.Run(() => Owned.Entry(id));
            Report.Note($"ledger {when}: {entry}");
            if (!Expect.That(entry.Known && entry.Live, $"the ledger has the item live {when}", entry.ToString()))
                return;
            Expect.Equal(client, entry.Owner, $"the ledger's owner {when} (the client)");
            if (where == "Inventory")
            {
                Expect.Equal("With/Inventory", entry.Place + "/" + entry.Hold, $"where the ledger has it {when}");
                Expect.Equal(client, entry.Holder, $"who the ledger says has it {when}");
            }
            else
                Expect.Equal(where, entry.Place, $"where the ledger has it {when}");
            Expect.Empty(await LedgerSave.SettledAudit(pair, new[] { id }), $"what the ledger disagrees with {when}");
        }
    }
}
