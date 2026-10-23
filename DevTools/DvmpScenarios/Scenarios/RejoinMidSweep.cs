using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A client comes back while Lost and Found sweeps up the mugs it left (owned-items.md, PRs 2a
    /// and 3). The client owns mugs on the ground and leaves (quits, or LEAVE=crash), with nobody
    /// near and a one-second grace, then rejoins at once. The sweep runs during the rejoin. Each mug
    /// must end up exactly once, still the client's with its new number, either in the world or in
    /// its Lost and Found under its own id. The ledger agrees, with no audit problems. The client
    /// retrieves one that was collected.
    /// </summary>
    [Scenario("rejoin-mid-sweep", "A client rejoining while its left mugs are swept into Lost and Found gets each back once, its own, in the world or its Lost and Found")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class RejoinMidSweep : Scenario<RejoinMidSweep.Options>
    {
        public sealed class Options
        {
            /// <summary>How the client leaves: quit or crash (its game killed).</summary>
            public string Leave = "quit";
            /// <summary>How many mugs the client leaves on the ground.</summary>
            public int Count = 4;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 50, Nearby = 30, Grace = 1 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            var nets = new ushort[o.Count];
            var ids = new string[o.Count];
            for (int i = 0; i < o.Count; i++)
            {
                nets[i] = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
                ids[i] = (await pair.Host.Run(() => Ownership.Give(nets[i], "client"))).Id;
            }
            // The client steps 40 m aside: near enough as their owner (50 m) that nothing is collected
            // while it is here, but out of the nearby-player protection (30 m), so the sweep starts
            // once it has gone and stops when it is back.
            await pair.Client.Run<Abs>(s => Walk.Away(s, 40, 0));
            if (!pair.Dedicated) await pair.Host.Run<Abs>(s => Walk.Away(s, 400, 20));
            Report.Note($"{o.Count} of the client's mugs on the ground");

            if (o.Leave == "crash") await pair.Client.Kill();
            else await pair.Client.Quit();
            await pair.Rejoin();
            await Task.Delay(10000);

            int lost = 0;
            ushort collected = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                OwnerInfo now = await pair.Host.Run(() => Ownership.ById(ids[i]));
                if (!Expect.That(now.Exists, "the mug is there after the rejoin", ids[i])) continue;
                if (now.Lost) { lost++; collected = now.NetId; }
                Expect.Equal(client, now.Owner, $"mug {now.NetId}'s owner after the rejoin (the client)");
                Expect.That(now.OwnerConnected, $"mug {now.NetId}'s owner (the client) has a session after the rejoin", now.Owner);
                Expect.Equal(client, now.LedgerOwner, $"the ledger's owner of mug {now.NetId}");
                Expect.That(now.Lost ? now.LostEntry == ids[i] : now.Placement == "World", "the mug is in the world or in Lost and Found under its own id", now.ToString());
            }
            Report.Note($"after the rejoin {lost} of {ids.Length} in Lost and Found");
            Expect.Empty(await pair.Host.Run<string[]>(s => Owned.AuditFor(s, ids)), "ledger audit problems for the mugs");
            if (collected != 0)
            {
                int slot = await pair.Client.Run<int>(s => LostAndFound.InGame.Retrieve(s, collected), 60);
                Report.Note($"the client retrieved {collected} into slot {slot}");
            }
        }
    }
}
