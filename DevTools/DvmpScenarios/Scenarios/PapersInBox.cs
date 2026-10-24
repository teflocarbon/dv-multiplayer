using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A job booklet in a box (docs/design/items/containers.md, "Job papers in a box"; R5 stack
    /// review F2). The client takes a job at FM's office and puts its booklet in its toolbox: the
    /// booklet is a row of the box, and no game keeps an object of it. The box is left on the
    /// ground, the host saves and both games restart: the booklet is still in the box, in its slot.
    /// The client takes it out while the job runs: it's the same booklet (its id), made again for
    /// its job. It goes back in, and the host abandons the job: the booklet's row is dropped from the
    /// box and its ledger entry ends. WHO=host: the host's own player does it (a player host only).
    /// </summary>
    [Scenario("papers-in-box", "F2: a job booklet in a box is a row, kept across a restart, comes out while its job runs, and is dropped once the job ends")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class PapersInBox : Scenario<PapersInBox.Options>
    {
        public sealed class Options
        {
            public string Station = "FM";
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
            /// <summary>The box the booklet goes into.</summary>
            public string Box = "ItemContainerToolbox";
            /// <summary>Who does it: client, or host (the host's own player; needs a player host).</summary>
            public string Who = "client";
        }

        // FM's office, as booklet-summon.
        private const float OfficeX = 5990f, OfficeZ = 6740f, AwayX = 8600f, AwayZ = 6740f;

        protected override async Task Run(Pair pair, Options o)
        {
            bool byHost = o.Who == "host";
            if (byHost && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            Peer who = byHost ? pair.Host : pair.Client;
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
            Report.Note($"the {who.Name} takes {job}");
            pair.Cleanup(() => pair.Host.Run(() => BookletSummon.InGame.Abandon(job)));
            ushort booklet = await who.Run<ushort>(s => BookletSummon.InGame.Accept(s, job), 60);
            string bookletId = await pair.Host.Run(() => Owned.IdOf(booklet));
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Box, o.Who));
            string boxId = await pair.Host.Run(() => Owned.IdOf(box));
            pair.Cleanup(async () =>
            {
                ushort left = await pair.Host.Run(() => Owned.NetIdOf(boxId));
                if (left != 0) await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { left }), 60);
            });
            Report.Note($"booklet {booklet} ({bookletId}), box {box} ({boxId})");
            Require.That(bookletId.Length > 0, "the booklet has an id");

            // Into the box: a row, and no object of it anywhere.
            Require.Equal(Boxes.Accepted, await who.Run<string>(s => Boxes.Deposit(s, box, booklet), 120), "the booklet into the box");
            await who.Run(() => Items.Drop(), 10);
            await Task.Delay(2000);
            Expect.Equal(boxId, await pair.Host.Run(() => Boxes.StoredIn(bookletId)), "the box the booklet is in");
            int slot = await pair.Host.Run(() => Boxes.StoredSlot(bookletId));
            Expect.Equal(0, await pair.Client.Run(() => InGame.Booklets(job)), "the client's booklet objects for the job, with it in the box");
            if (!pair.Dedicated)
                Expect.Equal(0, await pair.Host.Run(() => InGame.Booklets(job)), "the host's booklet objects for the job, with it in the box");

            // Across a restart.
            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart(o.Session);
            await Task.Delay(20000);   // the client receives its items; vanilla's reprint waits for the host's
            Expect.Equal(boxId, await pair.Host.Run(() => Boxes.StoredIn(bookletId)), "the box the booklet is in, after the restart");
            Expect.Equal(slot, await pair.Host.Run(() => Boxes.StoredSlot(bookletId)), "the booklet's slot, after the restart");
            Expect.Equal("ok", await pair.Host.Run(() => Boxes.GraphCheck()), "the graph after the restart");
            box = await pair.Host.Run(() => Owned.NetIdOf(boxId));
            Require.That(box != 0, "the box is in the world after the restart");

            // Out while its job runs: the same booklet, made again for its job.
            string taken = await who.Run<string>(s => Boxes.Withdraw(s, box, slot), 120);
            Report.Note("the booklet out: " + taken);
            Require.That(taken.StartsWith(Boxes.Accepted), "the booklet out of the box while its job runs", taken);
            booklet = ushort.Parse(taken.Substring(Boxes.Accepted.Length + 1));
            Expect.Equal(bookletId, await pair.Host.Run(() => Owned.IdOf(booklet)), "the booklet taken out keeps its id");
            Expect.Equal(job, await who.Run(() => InGame.JobOf(booklet)), "the booklet taken out is its job's");

            // Back in, and the job ends: its row goes.
            Require.Equal(Boxes.Accepted, await who.Run<string>(s => Boxes.Deposit(s, box, booklet), 120), "the booklet back into the box");
            await who.Run(() => Items.Drop(), 10);
            await pair.Host.Run(() => BookletSummon.InGame.Abandon(job));
            await Task.Delay(3000);
            Expect.Equal("", await pair.Host.Run(() => Boxes.StoredIn(bookletId)), "the box the booklet is in once its job ended (none)");
            LedgerEntry ended = await pair.Host.Run(() => Owned.Entry(bookletId));
            Report.Note($"the booklet's ledger entry once its job ended: known {ended.Known}, live {ended.Live}, ended {ended.Ended}");
            Expect.That(!ended.Live, "the booklet's ledger entry isn't live once its job ended", ended.Ended);
            Expect.Equal(0, await who.Run<int>(s => Boxes.Rows(s, box), 60), "the box's rows once the job ended");
            await who.Run(() => Items.Drop(), 10);
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>How many booklet objects this game has for the job.</summary>
            [Step]
            public static int Booklets(string jobId) =>
                Resources.FindObjectsOfTypeAll<JobBooklet>().Count(b => b != null && b.gameObject.scene.IsValid() && b.job?.ID == jobId);

            /// <summary>The job of this game's booklet object, "" for none.</summary>
            [Step]
            public static string JobOf(ushort netId) =>
                Multiplayer.Shared.Items.LiveState.NetworkedItem.TryGet(netId, out var item) && item != null
                    ? item.GetComponent<JobBooklet>()?.job?.ID ?? "" : "";
        }
    }
}
