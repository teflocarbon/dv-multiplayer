using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Lost and Found keeps several owners apart (owned-items.md, PRs 1b, 2a and 3). Mugs are given
    /// to the client (two), the host's own player (one, on a player host), and two players who
    /// aren't here (two and one), and all are collected. Each owner's Lost and Found must grow by
    /// exactly their mugs. Then the games save and restart. Every mug must come back once, under its
    /// own id, in its own owner's Lost and Found and bound at load: the absent players' with the
    /// absent number, the players' with their new numbers. The ledger agrees, with no audit problems.
    /// </summary>
    [Scenario("lost-and-found-many-owners", "Mugs of the client, the host and two absent players in Lost and Found stay each owner's, bound, across a save and restart")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class LostAndFoundManyOwners : Scenario<LostAndFoundManyOwners.Options>
    {
        public sealed class Options
        {
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            string host = pair.Dedicated ? "" : await pair.Host.Run(() => Owned.HostIdentity());
            string a = System.Guid.NewGuid().ToString("D"), b = System.Guid.NewGuid().ToString("D");
            var owners = new List<(string Name, string Owner, int Count)> { ("client", client, 2), ("absent A", a, 2), ("absent B", b, 1) };
            if (!pair.Dedicated) owners.Add(("host", host, 1));

            var baseline = new Dictionary<string, int>();
            foreach (var w in owners) baseline[w.Owner] = await pair.Host.Run(() => Ownership.LostCount(w.Owner));
            var mugs = new List<(string Id, string Owner, string Name)>();
            foreach (var w in owners)
                for (int i = 0; i < w.Count; i++)
                {
                    ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
                    string who = w.Name == "client" ? "client" : w.Name == "host" ? "host" : w.Owner;
                    OwnerInfo given = await pair.Host.Run(() => Ownership.Give(mug, who));
                    Require.Equal(w.Owner, given.Owner, $"the {w.Name}'s mug's owner");
                    Expect.That(await pair.Host.Run<bool>(s => Ownership.Collect(s, mug, "SaveRecovery")), $"the {w.Name}'s mug {mug} collected");
                    mugs.Add((given.Id, w.Owner, w.Name));
                }
            foreach (var w in owners)
                Expect.Equal(baseline[w.Owner] + w.Count, await pair.Host.Run(() => Ownership.LostCount(w.Owner)), $"the {w.Name}'s Lost and Found count after collecting");
            Report.Note($"{mugs.Count} mugs collected for {owners.Count} owners");

            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart(o.Session);
            await Task.Delay(15000);   // the client rejoins and Lost and Found binds

            foreach (var m in mugs)
            {
                OwnerInfo now = await pair.Host.Run(() => Ownership.ById(m.Id));
                if (!Expect.That(now.Exists, $"the {m.Name}'s mug is there after the restart", m.Id)) continue;
                Items.Track(now.NetId);
                Expect.Equal(m.Id, now.LostEntry, $"the {m.Name}'s mug's Lost and Found entry, bound at load");
                Expect.Equal(m.Owner, now.Owner, $"the {m.Name}'s mug's owner after the restart");
                Expect.Equal(m.Owner, now.LedgerOwner, $"the ledger's owner of the {m.Name}'s mug");
                Expect.Equal("LostAndFound", now.LedgerWhere, $"where the ledger has the {m.Name}'s mug");
                int number = m.Name == "client" ? now.ClientNumber : m.Name == "host" ? now.HostNumber : AbsentOwner.AbsentNumber;
                Expect.Equal(number, now.Number, $"the {m.Name}'s mug's owner number after the restart");
            }
            foreach (var w in owners)
                Expect.Equal(baseline[w.Owner] + w.Count, await pair.Host.Run(() => Ownership.LostCount(w.Owner)), $"the {w.Name}'s Lost and Found count after the restart");
            Expect.Empty(await pair.Host.Run<string[]>(s => Owned.AuditFor(s, mugs.Select(m => m.Id).ToArray())), "ledger audit problems for the mugs");
        }
    }
}
