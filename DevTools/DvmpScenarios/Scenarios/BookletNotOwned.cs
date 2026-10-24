using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Job papers are the job's, never owned (R6 C4; D5, OI-ID-12, OI-LF-17). The client takes a job
    /// at FM's office and carries its booklet a few metres: the booklet has no ledger entry and isn't
    /// in the client's owned list, though the client took it first. Everyone walks away: the booklet
    /// is collected into the Lost and Found of the player it was issued to, the client, who retrieves
    /// it from there. The client then puts it in a box of its own, which takes it unowned.
    /// </summary>
    [Scenario("booklet-not-owned", "A job booklet is never owned or listed; a lost one goes to the Lost and Found of the player it was issued to, and it still goes in a box (OI-ID-12, OI-LF-17)")]
    [Budget(600)]
    public sealed class BookletNotOwned : Scenario<BookletNotOwned.Options>
    {
        public sealed class Options
        {
            public string Station = "FM";
            /// <summary>The box the booklet goes into.</summary>
            public string Box = "ItemContainerToolbox";
        }

        // FM's office, as booklet-summon.
        private const float OfficeX = 5990f, OfficeZ = 6740f, AwayX = 8600f, AwayZ = 6740f;

        protected override async Task Run(Pair pair, Options o)
        {
            Peer[] players = pair.Dedicated ? new[] { pair.Client } : new[] { pair.Host, pair.Client };
            if (await pair.Host.Run(() => BookletSummon.InGame.AvailableJobs(o.Station)) == 0)
            {
                Report.Note($"no transport or empty-haul job at {o.Station}: going 2 km away so DV generates new ones");
                foreach (Peer p in players) await p.Run(() => Players.TeleportToGround(AwayX, AwayZ), 90);
                await Task.Delay(8000);
            }
            for (int i = 0; i < players.Length; i++)
            {
                float x = OfficeX - 4f * i;
                await players[i].Run(() => Players.TeleportToGround(x, OfficeZ), 90);
            }
            await Task.Delay(6000);   // the office's overviews load and reach the client

            string job = await pair.Host.Run(() => BookletSummon.InGame.PickJob(o.Station));
            pair.Cleanup(() => pair.Host.Run(() => BookletSummon.InGame.Abandon(job)));
            ushort booklet = await pair.Client.Run<ushort>(s => BookletSummon.InGame.Accept(s, job), 60);
            string bookletId = await pair.Host.Run(() => Owned.IdOf(booklet));
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            Report.Note($"the client takes {job}: booklet {booklet} ({bookletId})");
            Require.That(bookletId.Length > 0, "the booklet has an id");

            // Taken first by the client, and still nobody's.
            await pair.Client.Run(() => BookletSummon.InGame.CarryAway(booklet, OfficeX + 8f, OfficeZ), 60);
            ItemRecord taken = await pair.Host.Run(() => Items.Record(booklet));
            Report.Note("after the client carried it: " + taken);
            LedgerEntry entry = await pair.Host.Run(() => Owned.Entry(bookletId));
            Expect.That(!entry.Known, "the booklet has no ledger entry (OI-ID-12)", entry.Owner);
            OwnedList.Rows rows = await pair.Host.Run(() => OwnedList.InGame.ServerRows(client));
            Expect.That(System.Array.IndexOf(rows.Ids, bookletId) < 0, "the booklet isn't in the client's owned list");

            // Lost, it goes to the Lost and Found of the player it was issued to (OI-LF-17).
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 50, Nearby = 100, Grace = 1 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            await pair.Client.Run<Abs>(s => Walk.Away(s, 400, 0));
            if (!pair.Dedicated) await pair.Host.Run<Abs>(s => Walk.Away(s, 400, 20));
            ItemRecord lost = await Until.Value(() => pair.Host.Run(() => Items.Record(booklet)), r => r.Lost, 30, "the booklet collected into Lost and Found");
            Expect.That(!(await pair.Host.Run(() => Owned.Entry(bookletId))).Known, "the booklet in Lost and Found still has no ledger entry");
            int slot = await pair.Client.Run<int>(s => LostAndFound.InGame.Retrieve(s, lost.Handle), 60);
            Report.Note($"the client retrieved it into slot {slot}");
            ItemRecord back = await Until.Value(() => pair.Host.Run(() => Items.Record(booklet)), r => !r.Lost, 20, "the booklet out of Lost and Found");
            Expect.Equal("PlayerInventory", back.Placement, "the retrieved booklet's placement on the host");
            Expect.Equal(back.ClientId, back.PlacementPlayer, "the player the host has the retrieved booklet with (the client)");

            // Into a box of the client's, unowned (D5).
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Box, "client"));
            string boxId = await pair.Host.Run(() => Owned.IdOf(box));
            pair.Cleanup(async () =>
            {
                ushort left = await pair.Host.Run(() => Owned.NetIdOf(boxId));
                if (left != 0) await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { left }), 60);
            });
            Expect.Equal(Boxes.Accepted, await pair.Client.Run<string>(s => Boxes.Deposit(s, box, booklet), 120), "the unowned booklet into the client's box");
            await pair.Client.Run(() => Items.Drop(), 10);
            await Task.Delay(2000);
            Expect.Equal(boxId, await pair.Host.Run(() => Boxes.StoredIn(bookletId)), "the box the booklet is in");
            Expect.That(!(await pair.Host.Run(() => Owned.Entry(bookletId))).Known, "the booklet in the box still has no ledger entry");
        }
    }
}
