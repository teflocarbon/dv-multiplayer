using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Job = DV.Logic.Job.Job;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The job validator's reprint button brings a player's booklet back (docs/jobs.md, "Reprint"):
    /// a player takes a transport or empty-haul job at the station office, carries its booklet
    /// 60 m away and leaves it there, comes back and presses the validator's reprint button. On both
    /// games the booklet is back at the printer within 15 s, with the same NetId, and the job has as
    /// many booklets as before (moved, not printed again). The press is a request to the server
    /// from every player, the host's own included (`IJobsClient`): with no client on the
    /// host (stage 3), the host's press once ran vanilla's summon instead, which moved the booklet on
    /// the host only. The job is abandoned afterwards.
    ///   WHO=client (default) or host: who takes the job and presses the button; host needs a player
    ///   host. STATION=FM.
    ///   FAR=1 (with WHO=host): the host stashes its booklet and the client goes 2.6 km away; the press
    ///   makes the booklet from its row at the printer, and its job's update has every game make a
    ///   copy, near it or not. The host stashes it again: the far client must have no copy of it
    ///   left (R4C C9 B5: only the games that knew the paper were told it went). The holder is the
    ///   host's player because the far game must be another than the server's.
    /// </summary>
    [Scenario("booklet-summon", "The validator's reprint button brings the presser's booklet back to the printer on both games")]
    [Budget(240)]
    public sealed class BookletSummon : Scenario<BookletSummon.Options>
    {
        public sealed class Options
        {
            /// <summary>client or host: who takes the job and presses the button.</summary>
            public string Who = "client";
            public string Station = "FM";
            /// <summary>The booklet is pressed out of the host's slot with the client far away, and stashed again.</summary>
            public bool Far;
        }

        // FM's office, by the validator and the overviews, and a place 2.6 km away (as `jobs`).
        private const float OfficeX = 5990f, OfficeZ = 6740f, AwayX = 8600f, AwayZ = 6740f;

        protected override async Task Run(Pair pair, Options o)
        {
            bool host = o.Who == "host";
            Require.That(host || o.Who == "client", "WHO is client or host", o.Who);
            if (host && pair.Dedicated)
            {
                Report.Note("a dedicated host has no player to press the button: skipped");
                return;
            }
            if (o.Far && !host) throw new HarnessException("FAR=1 needs WHO=host: the far game must be another than the server's");
            Peer who = host ? pair.Host : pair.Client;
            Peer[] players = pair.Dedicated ? new[] { pair.Client } : new[] { pair.Host, pair.Client };

            // A job left taken at the station stops DV generating new ones there until every player
            // has been 2 km away (StationController.Update).
            if (await pair.Host.Run(() => InGame.AvailableJobs(o.Station)) == 0)
            {
                Report.Note($"no transport or empty-haul job at {o.Station}: going 2 km away so DV generates new ones");
                foreach (Peer p in players) await p.Run(() => Players.TeleportToGround(AwayX, AwayZ), 90);
                await Task.Delay(8000);   // the station notices everyone has gone
            }
            for (int i = 0; i < players.Length; i++)
            {
                float x = OfficeX - 4f * i;
                await players[i].Run(() => Players.TeleportToGround(x, OfficeZ), 90);
            }
            await Task.Delay(6000);   // the office's overviews load and reach the client

            string job = await pair.Host.Run(() => InGame.PickJob(o.Station));
            Report.Note($"{o.Who} takes {job}");
            pair.Cleanup(() => pair.Host.Run(() => InGame.Abandon(job)));
            ushort booklet = await who.Run<ushort>(s => InGame.Accept(s, job), 60);
            Report.Note($"booklet {booklet}");
            if (o.Far)
            {
                await Far(pair, job, o.Station, booklet);
                return;
            }

            await who.Run(() => InGame.CarryAway(booklet, OfficeX + 60f, OfficeZ), 90);
            await who.Run(() => Players.TeleportToGround(OfficeX, OfficeZ), 90);
            PeerResults<Booklets> before = await pair.Both.Run(() => InGame.BookletState(job, o.Station, booklet));
            Report.Note($"before: host {before.Host}; client {before.Client}");
            foreach ((string peer, Booklets b) in before)
                Require.AtLeast(b.Distance, 20, $"{peer}: the booklet's distance from the printer before the press (m)");

            await who.Run(() => InGame.Reprint(o.Station));
            foreach ((string peer, Peer p) in new[] { ("host", pair.Host), ("client", pair.Client) })
                await Until.Value(() => p.Run(() => InGame.BookletState(job, o.Station, booklet)), b => b.Distance < 2f, 15,
                    $"{peer}: booklet {booklet} back at the printer (m)");
            PeerResults<Booklets> after = await pair.Both.Run(() => InGame.BookletState(job, o.Station, booklet));
            Report.Note($"after: host {after.Host}; client {after.Client}");
            foreach ((string peer, Booklets b) in after)
                Expect.Equal(before.Host.Count, b.Count, $"{peer}: the job's booklets after the press (moved, not printed again)");
        }

        // FAR=1: the host's booklet pressed out of its slot while the client is far away, then stashed again.
        private static async Task Far(Pair pair, string job, string station, ushort booklet)
        {
            int slot = await pair.Host.Run<int>(s => Items.Stash(s, booklet, false), 60);
            Report.Note($"the host stashed booklet {booklet} in slot {slot}; the client goes 2.6 km away");
            await pair.Client.Run(() => Players.TeleportToGround(AwayX, AwayZ), 90);
            await Task.Delay(3000);
            await pair.Host.Run(() => InGame.Reprint(station));
            await Until.Value(() => pair.Host.Run(() => InGame.BookletState(job, station, booklet)), b => b.Distance >= 0 && b.Distance < 2f, 15,
                $"host: booklet {booklet} out of its slot, at the printer (m)");
            int copied = await pair.Client.Run(() => InGame.Copies(booklet));
            Report.Note($"the far client has {copied} copy(ies) of it while it's at the printer" + (copied == 0 ? ": nothing to leave behind, the check below proves nothing" : ""));
            int again = await pair.Host.Run<int>(s => Items.Stash(s, booklet, false), 60);
            Report.Note($"the host stashed it again, in slot {again}");
            await Task.Delay(3000);   // it goes to data, and every game is told
            Expect.Zero(await pair.Client.Run(() => InGame.Copies(booklet)), $"the far client's copies of booklet {booklet}, back in the host's slot (B5)");
            Expect.Zero(await pair.Host.Run(() => InGame.Copies(booklet)), $"the host's objects of booklet {booklet}, back in its slot");
        }

        public sealed class Booklets
        {
            /// <summary>The job's booklets on this game.</summary>
            public int Count;
            /// <summary>The watched booklet's distance from the validator's printer (m); -1 if it isn't here.</summary>
            public float Distance;
            public override string ToString() => $"{Count} booklet(s), the watched one {Distance:0.#} m from the printer";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: clears payable debts (test saves pile them up and vanilla then refuses
            /// jobs) and counts the station's available transport and empty-haul jobs.</summary>
            [Step]
            public static int AvailableJobs(string station)
            {
                var debts = DV.ServicePenalty.CareerManagerDebtController.Instance;
                debts.ClearRestOfThePayableDebts();
                debts.RefreshExistingDebtsState();
                return Candidates(station).Count();
            }

            /// <summary>Host: an available transport or empty-haul job at the station whose overview
            /// is lying ready.</summary>
            [Step]
            public static string PickJob(string station) =>
                Candidates(station).FirstOrDefault(j => NetworkedJob.TryGetFromJob(j, out NetworkedJob n) && n.JobOverview != null &&
                                                        n.JobOverview.GetItemState() == ItemState.Dropped)?.ID
                ?? throw new System.InvalidOperationException("no available transport or empty-haul job with an overview at " + station);

            /// <summary>This player: takes the job's overview and puts it in the nearest validator, as
            /// taking a job does; returns the booklet it was issued.</summary>
            [Step]
            public static IEnumerator Accept(Step<ushort> step, string jobId)
            {
                NetworkedJob nj = Resources.FindObjectsOfTypeAll<NetworkedJob>().First(n => n.Job?.ID == jobId);
                NetworkedItem overview = nj.JobOverview ?? throw new System.InvalidOperationException("no overview for " + jobId);
                JobValidator validator = Object.FindObjectsOfType<JobValidator>()
                    .OrderBy(v => Vector3.Distance(v.transform.position, PlayerManager.PlayerTransform.position)).First();
                if (Hands.Held != null && Hands.Held.NetId != overview.NetId) yield return Hands.Drop();
                if (Hands.Held == null) yield return Hands.Pickup(overview.NetId);
                for (int i = 0; i < 20; i++) yield return null;
                validator.ProcessJobOverview(overview.GetComponent<JobOverview>());
                yield return Wait.Until(() => nj.Job.State == DV.ThingTypes.JobState.InProgress, 10, "the job to start");
                NetworkedItem booklet = null;
                yield return Wait.Until(() => (booklet = Object.FindObjectsOfType<JobBooklet>().Where(b => b.job == nj.Job)
                    .Select(b => b.GetComponent<NetworkedItem>()).FirstOrDefault(n => n != null && n.NetId != 0)) != null, 10, "the job's booklet");
                Hands.ReleaseControl();
                step.Result = booklet.NetId;
            }

            /// <summary>This player: picks the booklet up, takes it to (x, z) and drops it there.</summary>
            [Step]
            public static IEnumerator CarryAway(ushort booklet, float x, float z)
            {
                if (Hands.Held != null && Hands.Held.NetId != booklet) yield return Hands.Drop();
                if (Hands.Held == null)
                {
                    yield return LocalPlayer.GoTo(Items.Find(booklet));
                    yield return Hands.Pickup(booklet);
                }
                // On bare ground: a spot with a parked car would put the booklet on the car, which DV
                // switches off out of the players' range (the run then found no booklet at all).
                (float x, float z) spot = new[] { (0f, 0f), (0f, 20f), (0f, -20f), (20f, 0f), (20f, 20f), (20f, -20f), (40f, 0f) }
                    .Select(o => (x: x + o.Item1, z: z + o.Item2))
                    .FirstOrDefault(s => !Object.FindObjectsOfType<TrainCar>().Any(c =>
                    {
                        Vector3 p = c.transform.position - WorldMover.currentMove;
                        return new Vector2(p.x - s.x, p.z - s.z).magnitude < 15f;
                    }));
                if (spot == default) throw new System.Exception($"no spot clear of cars near ({x:F0}, {z:F0})");
                yield return LocalPlayer.TeleportToGround(spot.x, spot.z);
                yield return Hands.Drop();
                yield return new WaitForSecondsRealtime(2f);
                Hands.ReleaseControl();
                if (Items.Find(booklet)?.GetComponentInParent<TrainCar>() != null)
                    throw new System.Exception($"booklet {booklet} landed on a train car at ({spot.x:F0}, {spot.z:F0})");
            }

            /// <summary>This player: presses the station validator's reprint button.</summary>
            [Step]
            public static void Reprint(string station) => Validator(station).SummonAllActiveJobBooklets();

            /// <summary>Either peer: the job's booklets here, and how far the watched one is from the
            /// station validator's printer.</summary>
            [Step]
            public static Booklets BookletState(string jobId, string station, ushort watched)
            {
                Job job = Resources.FindObjectsOfTypeAll<NetworkedJob>().FirstOrDefault(n => n.Job?.ID == jobId)?.Job;
                // Inactive ones too: a booklet left on a train car far from every player is switched
                // off by DV's train LOD (TrainItemActivityHandler) but still exists.
                JobBooklet[] booklets = Resources.FindObjectsOfTypeAll<JobBooklet>()
                    .Where(b => b.gameObject.scene.IsValid() && job != null && b.job == job).ToArray();
                NetworkedItem item = booklets.Select(b => b.GetComponent<NetworkedItem>()).FirstOrDefault(n => n != null && n.NetId == watched);
                Vector3 printer = Validator(station).bookletPrinter.spawnAnchor.position;
                return new Booklets
                {
                    Count = booklets.Length,
                    Distance = item == null ? -1f : Vector3.Distance(item.transform.position, printer),
                };
            }

            /// <summary>Either peer: the booklet objects here (inactive ones too) with this NetId; a stand-in isn't one.</summary>
            [Step]
            public static int Copies(ushort netId) =>
                Resources.FindObjectsOfTypeAll<JobBooklet>().Count(b => b.gameObject.scene.IsValid() &&
                    b.GetComponent<Multiplayer.Shared.Items.Inventory.InventoryStandIn>() == null &&
                    b.GetComponent<NetworkedItem>()?.NetId == netId);

            /// <summary>Host: abandons the job, so the station generates new ones for the next run.</summary>
            [Step]
            public static void Abandon(string jobId)
            {
                Job job = Resources.FindObjectsOfTypeAll<NetworkedJob>().FirstOrDefault(n => n.Job?.ID == jobId)?.Job;
                if (job != null && job.State == DV.ThingTypes.JobState.InProgress)
                    DV.Logic.Job.JobsManager.Instance.AbandonJob(job);
            }

            private static StationController Station(string yard) =>
                StationController.allStations.First(s => s.stationInfo.YardID == yard);

            private static System.Collections.Generic.IEnumerable<Job> Candidates(string station) =>
                Station(station).logicStation.availableJobs
                    .Where(j => j.jobType == DV.ThingTypes.JobType.Transport || j.jobType == DV.ThingTypes.JobType.EmptyHaul);

            private static JobValidator Validator(string station) =>
                NetworkedStationController.GetFromStation(Station(station).logicStation, out NetworkedStationController s) && s.JobValidator != null
                    ? s.JobValidator
                    : throw new System.InvalidOperationException("no job validator at " + station);
        }
    }
}
