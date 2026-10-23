using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Trains;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Re-rolling an offered job by dropping its overview in the station office bin (docs/jobs.md).
    /// 1. WHO drops an offered job's overview in the bin with nobody on its cars: on both peers the
    ///    job expires and its overview and cars are gone at once, and the station offers a new job in
    ///    its place. Both peers end up offering the same jobs. Vanilla's generator can fail (30 tries,
    ///    licences and track space): a job binned without a replacement still has to expire with its
    ///    cars, and the next one binned must get one (3 tries).
    /// 2. The client boards a car of another offered job and WHO bins that job's overview: the paper
    ///    flies back up out of the bin and lands at the same spot on both peers, and the job, its
    ///    overview and its cars stay. With WHO=client the client lets go of the paper on the car and
    ///    it's recorded in the car's interior before it lands in the bin (the server takes it out of
    ///    the interior first, or the throw was lost).
    /// On a dedicated host only WHO=client applies.
    /// </summary>
    [Scenario("job-reroll", "An overview binned with nobody on its job's cars re-rolls the job on both peers; binned with a player on a car, it's thrown back out and the job stays")]
    [Budget(600)]
    public sealed class JobReroll : Scenario<JobReroll.Options>
    {
        public sealed class Options
        {
            /// <summary>Who bins the overview: client or host.</summary>
            public string Who = "client";
            /// <summary>The station's yard ID.</summary>
            public string Station = "FM";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Peer who = Places.Player(pair, o.Who, "WHO");
            pair.Cleanup(async () =>
            {
                await who.Run(() => Players.EmptyHands());
                await Places.Office(pair, 4f);
            });

            await Places.Office(pair, 4f);
            for (int attempt = 1; ; attempt++)
            {
                Report.Note($"1. {o.Who} bins an offered job's overview, nobody on its cars (try {attempt})");
                Picked p = await Pick(pair, o.Station);
                Report.Note($"{p.JobId} (job {p.Job}, overview {p.Overview}), cars {string.Join(",", p.Cars)}; {p.Offered} of {p.Capacity} jobs offered");
                Binned b = await who.Run<Binned>(s => InGame.Bin(s, p.Job, false));
                Report.Note($"bin at {b.Bin} (size {b.BinSize}): went in {b.WentIn}, paper gone {b.Gone}");
                // (The host's own request is handled in-process: its paper can go the frame it reaches the bin.)
                Require.That(b.Gone, "the binned paper was taken away (the job re-rolled)");
                await Task.Delay(2000);
                // The station generates the replacement a job at a time, and its packets reach the client.
                await pair.Host.Run(() => InGame.WaitForGeneration(o.Station, 30f), 60);
                JobState h = await pair.Host.Run(() => InGame.State(p.Job, p.Cars, o.Station));
                JobState c = await pair.Client.Run<JobState>(s => InGame.WaitForOffered(s, p.Job, p.Cars, o.Station, h.Offered, 15f), 45);
                bool ok = true;
                foreach (var (peer, s) in new[] { ("host", h), ("client", c) })
                {
                    Report.Note($"{peer}: job {s.State}, overview {s.Overview}, cars left: {s.CarsLeft}; offered {s.Offered}");
                    ok &= Expect.That(s.State is "Expired" or "gone", $"{peer}: the binned job expired", s.State);
                    ok &= Expect.Equal("none", s.Overview, $"{peer}: the binned job's overview");
                    ok &= Expect.Equal(0, s.CarsLeft, $"{peer}: the binned job's cars left ({s.Cars})");
                }
                ok &= Expect.Equal(h.Offered, c.Offered, "the client offers the host's jobs");
                if (!ok) return;
                if (h.OfferedCount >= p.Offered) break;
                Report.Note($"no replacement: the station offers {h.OfferedCount} jobs, {p.Offered} before (the generator found none)");
                Require.That(attempt < 3, "a replacement was generated for one of three jobs binned in a row", "none was");
            }

            Report.Note($"2. the client boards a car of another job; {o.Who} bins its overview");
            await Places.Office(pair, 4f);
            Picked q = await Pick(pair, o.Station);
            Report.Note($"{q.JobId} (job {q.Job}, overview {q.Overview}), the client boards {q.NearCar} ({q.NearCarMetres} m from the bin)");
            if (o.Who == "client") await pair.Client.Run(() => InGame.Grab(q.Overview), 30);
            await pair.Client.Run(() => Players.Board(q.NearCar));
            await Task.Delay(1000);
            // With WHO=client, let the drop reach the server first, so the paper let go on the car is
            // recorded in the car's interior when it lands in the bin.
            Binned t = await who.Run<Binned>(s => InGame.Bin(s, q.Job, o.Who == "client"));
            Report.Note($"went in {t.WentIn}, paper gone {t.Gone}, flew {t.FlewUp} m up, landed {t.FromBin} m from the bin");
            // (The host's own request is handled in-process: its paper can be thrown out the frame it reaches the bin.)
            Require.That(!t.Gone, "the paper is still there (not re-rolled with a player on its car)");
            Expect.AtLeast(t.FlewUp, 0.45, "how far the paper was thrown back up out of the bin (m)");
            Expect.That(!t.InBin, "the paper didn't end up in the bin");
            await Task.Delay(4000);
            PeerResults<JobState> after = await pair.Both.Run(() => InGame.State(q.Job, q.Cars, o.Station));
            foreach (var (peer, s) in after)
            {
                Report.Note($"{peer}: job {s.State}, overview {s.Overview} at {s.OverviewAt}, cars left: {s.CarsLeft}/{q.Cars.Length}");
                Expect.Equal("Available", s.State, $"{peer}: the job is still offered");
                Expect.That(s.Overview != "none", $"{peer}: has the job's overview", s.Overview);
                Expect.Equal(q.Cars.Length, s.CarsLeft, $"{peer}: the job's cars left");
            }
            if (after.Host.OverviewAt != null && after.Client.OverviewAt != null)
                Expect.That(Peers.Distance(after.Host.OverviewAt, after.Client.OverviewAt) < 0.5, "the paper landed at the same spot on both peers",
                    $"{Peers.Distance(after.Host.OverviewAt, after.Client.OverviewAt):0.00} m apart, expected under 0.5");
        }

        /// <summary>An offered job at the office that can be re-rolled; DV makes new ones once every
        /// player has been 2 km away.</summary>
        private static async Task<Picked> Pick(Pair pair, string station)
        {
            Picked p = await pair.Host.Run(() => InGame.Pick(station));
            if (p.Job != 0) return p;
            Report.Note($"no job to re-roll at {station} ({p.Offered} offered): going 2.6 km away so DV generates new ones");
            await Places.Away(pair);
            await Places.Office(pair, 4f);
            await Task.Delay(15000);   // the station generates its jobs and prints their overviews
            p = await pair.Host.Run(() => InGame.Pick(station));
            Require.That(p.Job != 0, $"an offered job at {station} that can be re-rolled", $"none of {p.Offered} offered");
            return p;
        }

        public sealed class Picked
        {
            /// <summary>The job's net ID; 0 when there is none to re-roll.</summary>
            public ushort Job;
            public string JobId;
            public ushort Overview;
            /// <summary>"ID:netId" for each car: an ID alone can come back on a new car.</summary>
            public string[] Cars;
            /// <summary>The job's car nearest the office bin: a player riding it stays in range of the
            /// bin's items (a car out in the yard isn't).</summary>
            public string NearCar;
            public int NearCarMetres, Offered, Capacity;
        }

        public sealed class Binned
        {
            public bool Gone, WentIn, InBin;
            public ushort Overview;
            /// <summary>How far above the bin's top the paper got (m): let go 0.25 m above it, so higher means thrown.</summary>
            public float FlewUp;
            /// <summary>Where it landed, from the bin's top on the ground (m); -1 when it's gone.</summary>
            public float FromBin;
            public string Bin, BinSize;
        }

        public sealed class JobState
        {
            public string State, Overview, Cars;
            public Abs OverviewAt;
            public int CarsLeft, OfferedCount;
            /// <summary>The station's offered job IDs, sorted, comma-separated.</summary>
            public string Offered;
            public bool Generating;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: clears payable debts (vanilla refuses jobs with them) and picks an offered job at the
            /// station that can be re-rolled: its overview lies where it was printed, and every one of its
            /// cars is there with nobody on them. Prefers the job with a car nearest the office bin.
            /// Result: Job 0 when there's none.
            /// </summary>
            [Step]
            public static Picked Pick(string station)
            {
                Jobs.ClearDebts();
                var st = StationController.allStations.First(s => s.stationInfo.YardID == station);
                var validator = NetworkedStationController.GetFromStationController(st, out var nst) ? nst.JobValidator : null;
                var bin = UnityEngine.Object.FindObjectsOfType<JobAbandoner>()
                    .OrderBy(b => Vector3.Distance(b.transform.position, validator != null ? validator.transform.position : st.transform.position)).First();
                var registry = TrainCarRegistry.Instance.logicCarToTrainCar;
                var players = Multiplayer.Server.Session.NetworkServer.Instance.ServerPlayers.Select(p => p.CarId).ToHashSet();
                var candidates = new List<(NetworkedJob nj, List<TrainCar> cars, TrainCar near, float distance)>();
                foreach (var job in st.logicStation.availableJobs.ToList())
                {
                    if (!NetworkedJob.TryGetFromJob(job, out var nj) || nj.JobOverview == null || nj.JobOverview.GetItemState() != ItemState.Dropped)
                        continue;
                    if (!DV.Logic.Job.JobsManager.Instance.jobToJobCars.TryGetValue(job, out var logicCars) || logicCars.Count == 0)
                        continue;
                    var cars = logicCars.Select(c => registry.TryGetValue(c, out var tc) ? tc : null).ToList();
                    if (cars.Any(tc => tc == null || !NetworkedTrainCar.TryGetFromTrainCar(tc, out var n) || n.HasPlayers || players.Contains(n.NetId)))
                        continue;
                    var near = cars.OrderBy(tc => Vector3.Distance(tc.transform.position, bin.transform.position)).First();
                    candidates.Add((nj, cars, near, Vector3.Distance(near.transform.position, bin.transform.position)));
                }
                if (candidates.Count == 0)
                    return new Picked { Offered = st.logicStation.availableJobs.Count };
                var pick = candidates.OrderBy(c => c.distance).First();
                return new Picked
                {
                    Job = pick.nj.NetId, JobId = pick.nj.Job.ID, Overview = pick.nj.JobOverview.NetId,
                    Cars = pick.cars.Select(tc => tc.ID + ":" + tc.GetComponent<NetworkedTrainCar>().NetId).ToArray(),
                    NearCar = pick.near.ID, NearCarMetres = (int)pick.distance,
                    Offered = st.logicStation.availableJobs.Count, Capacity = st.proceduralJobsRuleset.jobsCapacity
                };
            }

            /// <summary>The client: picks up the overview where it stands (before boarding a car with it).</summary>
            [Step]
            public static IEnumerator Grab(ushort overview) => Hands.Pickup(overview);

            /// <summary>
            /// Either player: picks up a job's overview, lets go of it above the office bin by its
            /// station's validator (vanilla's JobAbandoner) and lets it fall in, as a player throwing it
            /// away does. Waits for the paper to be taken away (re-rolled) or thrown back out.
            /// <paramref name="settle"/>: let the drop reach the server first.
            /// </summary>
            [Step]
            public static IEnumerator Bin(Step<Binned> step, ushort job, bool settle)
            {
                var nj = Resources.FindObjectsOfTypeAll<NetworkedJob>().First(n => n != null && n.NetId == job);
                var item = nj.JobOverview ?? throw new Exception("no overview for " + nj.Job.ID);
                ushort netId = item.NetId;
                var validator = nj.Station.JobValidator ?? throw new Exception("no validator at " + nj.Station.name);
                var bin = UnityEngine.Object.FindObjectsOfType<JobAbandoner>()
                    .OrderBy(b => Vector3.Distance(b.transform.position, validator.transform.position)).First();
                var trigger = bin.GetComponent<Collider>();
                Bounds bounds = trigger.bounds;
                Vector3 above = bounds.center + Vector3.up * (bounds.extents.y + 0.25f);

                // (Already in hand when the player picked it up before boarding a car.)
                if (Hands.Held == null || Hands.Held.NetId != netId)
                {
                    if (Hands.Held != null) yield return Hands.Drop();
                    yield return Hands.Pickup(netId);
                }
                for (int i = 0; i < 10; i++) yield return null;
                yield return Hands.Drop();
                Hands.ReleaseControl();
                if (settle) yield return new WaitForSeconds(1.5f);
                // Let go on a car, the paper is the car's (DV parents it to the car the player stands on);
                // one that really flew to the bin would have left the car on the way. Put it in the world.
                item.ParentToWorld();
                var body = item.Item.ItemRigidbody;
                body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero;
                body.position = above; item.transform.position = above;

                // It drops in; then it's either destroyed with its job or thrown up and out.
                // (Applying the throw deactivates the paper for a frame: only a destroyed one is gone.)
                float highest = 0f; bool wentIn = false;
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 8f;)
                {
                    if (item == null) break;
                    if (bounds.Contains(item.transform.position)) wentIn = true;
                    highest = Mathf.Max(highest, item.transform.position.y - (bounds.center.y + bounds.extents.y));
                    yield return null;
                }
                bool gone = item == null;
                Vector3 binTop = bounds.center + Vector3.up * bounds.extents.y;
                step.Result = new Binned
                {
                    Gone = gone, WentIn = wentIn, Overview = netId,
                    FlewUp = (float)Math.Round(highest, 2),
                    FromBin = gone ? -1 : (float)Math.Round(Vector3.Distance(new Vector3(item.transform.position.x, 0, item.transform.position.z), new Vector3(binTop.x, 0, binTop.z)), 2),
                    InBin = !gone && bounds.Contains(item.transform.position),
                    Bin = Dev.Abs(bin.transform.position).ToString("F1"), BinSize = bounds.size.ToString("F2")
                };
            }

            /// <summary>Host: waits until the station isn't generating jobs, for at most
            /// <paramref name="seconds"/> (and carries on if it still is).</summary>
            [Step]
            public static IEnumerator WaitForGeneration(string station, float seconds)
            {
                var st = StationController.allStations.First(s => s.stationInfo.YardID == station);
                for (float t0 = Time.realtimeSinceStartup; st.ProceduralJobsController.IsJobGenerationActive && Time.realtimeSinceStartup - t0 < seconds;)
                    yield return new WaitForSecondsRealtime(1f);
            }

            /// <summary>Client: waits until it offers <paramref name="offered"/> (the host's jobs), for at
            /// most <paramref name="seconds"/>; result: its state then.</summary>
            [Step]
            public static IEnumerator WaitForOffered(Step<JobState> step, ushort job, string[] cars, string station, string offered, float seconds)
            {
                for (float t0 = Time.realtimeSinceStartup; State(job, cars, station).Offered != offered && Time.realtimeSinceStartup - t0 < seconds;)
                    yield return new WaitForSecondsRealtime(1f);
                step.Result = State(job, cars, station);
            }

            /// <summary>Either peer: a job's state and overview, which of its cars still exist, and the
            /// station's offered jobs.</summary>
            [Step]
            public static JobState State(ushort job, string[] cars, string station)
            {
                var nj = Resources.FindObjectsOfTypeAll<NetworkedJob>().FirstOrDefault(n => n != null && n.NetId == job);
                var ov = nj?.JobOverview;
                bool hasOverview = ov != null && ov.gameObject.activeInHierarchy;
                var live = UnityEngine.Object.FindObjectsOfType<NetworkedTrainCar>()
                    .Where(n => n.TrainCar != null && cars.Contains(n.TrainCar.ID + ":" + n.NetId)).Select(n => n.TrainCar.ID).ToList();
                var st = StationController.allStations.First(s => s.stationInfo.YardID == station);
                return new JobState
                {
                    State = nj?.Job == null ? "gone" : nj.Job.State.ToString(),
                    Overview = hasOverview ? ov.NetId + ":" + ov.GetItemState() : "none",
                    OverviewAt = hasOverview ? Positions.Of(ov.transform.position) : null,
                    Cars = string.Join(",", live), CarsLeft = live.Count,
                    Offered = string.Join(",", st.logicStation.availableJobs.Select(j => j.ID).OrderBy(id => id)),
                    OfferedCount = st.logicStation.availableJobs.Count,
                    Generating = st.ProceduralJobsController.IsJobGenerationActive
                };
            }
        }
    }
}
