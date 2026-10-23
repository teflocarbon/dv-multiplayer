using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Job = DV.Logic.Job.Job;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data.Jobs;
using Multiplayer.Shared.Trains;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The host's job trains stay coupled (the "cargo containers randomly decouple" family): the
    /// host's own client used to apply packets the server meant for remote clients to the host's
    /// world, and a train-sync snapshot from before a spawn's auto-couple uncoupled the host's
    /// consists. Loading a loco's cab asked for such a snapshot, so the host stepping into the cab of
    /// a loco on a job train uncoupled that train (docs/design/host-local-session.md, stage 1).
    /// 1. The station's job generator spawns fresh jobs, as when a player arrives (its available jobs
    ///    are expired, then it generates new ones, up to three rounds); the host takes a new job it is
    ///    licensed for: transport or empty haul if there is one, else any with cars (shunting jobs too).
    /// 2. A shunter rolls onto the job's train and the host couples it, as a player does.
    /// 3. The host loads the loco's cab RELOADS times from beside it, then boards it.
    /// 4. For HOLD seconds, the host's and the client's coupling of the train (and the loco) must stay
    ///    exactly as it was after step 2, and match each other.
    /// The host's own client must also never have been sent a train packet (the server-side half of
    /// the same rule: HostOwnClient logs one it drops). Needs a player host: a dedicated host has no
    /// player to take the job.
    /// </summary>
    [Scenario("host-job-train-coupled", "The host takes a fresh job, couples a loco to its train and steps into the cab: the train stays coupled the same way on both games")]
    [Needs(Need.PlayerHost)]
    [Budget(900)]
    public sealed class HostJobTrainCoupled : Scenario<HostJobTrainCoupled.Options>
    {
        public sealed class Options
        {
            /// <summary>The station's yard ID.</summary>
            public string Station = "FM";
            /// <summary>The shunter coupled to the job's train.</summary>
            public string Loco = "LocoDE2";
            /// <summary>How many times the host loads the loco's cab before boarding.</summary>
            public int Reloads = 3;
            /// <summary>Seconds the coupling is watched.</summary>
            public int Hold = 60;
        }

        // A train packet the host's own client dropped (it logs each type once a session).
        private static readonly Regex DroppedTrainPacket = new(@"Ignored (Clientbound[A-Za-z]*)");

        protected override async Task Run(Pair pair, Options o)
        {
            long cursor = await pair.Host.LogCursor();
            string loco = null;
            pair.Cleanup(async () =>
            {
                if (loco != null) await pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco }));
            });
            // Off the loco before it's deleted (cleanups run last first).
            pair.Cleanup(async () =>
            {
                await pair.Host.Run(() => Players.TeleportToGround(7194f, 6873f));
                await pair.Client.Run(() => Players.TeleportToGround(7190f, 6880f));
            });

            // Fresh jobs, as when a player arrives at a station: its offered jobs are expired, both
            // players go 2.6 km away (DV deletes expired jobs' cars only once nobody is near, and
            // earlier runs' job trains left the generator no room: 30 failed tries, three rounds) and
            // come back, and the station generates new ones.
            string[] old = await pair.Host.Run(() => InGame.Expire(o.Station));
            await Places.Away(pair);
            await Places.Office(pair, 6f);

            Generated g = await pair.Host.Run<Generated>(s => InGame.Generate(s, o.Station, old), 300);
            Require.That(g.Cars.Length > 0, $"{g.Job} has cars");
            Topology t = await Both(pair, g.Cars);
            Report.Note($"generated: {g.Rounds}");
            Report.Note($"{g.Job} ({g.Type}), cars {string.Join(",", g.Cars)}: {t.Topo} ({t.Couplings} coupling(s))");

            Accepted a = await pair.Host.Run<Accepted>(s => Jobs.Accept(s, g.Job));
            Require.Equal("InProgress", a.State, "the job's state once the host took it");
            // Given back after the run: a job left taken keeps its train on the station's tracks, and the
            // next run's generator finds no room.
            pair.Cleanup(() => pair.Host.Run(() => InGame.Abandon(g.Job)));
            await Task.Delay(2000);
            Topology t2 = await Both(pair, g.Cars);
            Require.Equal(t.Topo, t2.Topo, "the train's coupling after taking the job");

            Coupled l = await pair.Host.Run<Coupled>(s => InGame.CoupleLoco(s, g.Cars, o.Loco), 150);
            loco = l.Loco;
            Report.Note($"{l.Loco} coupled to {l.End} ({l.How})");
            await Task.Delay(3000);
            ushort[] all = g.Cars.Append(l.LocoNetId).ToArray();
            Topology want = await Both(pair, all);
            // Exactly one new coupling, the loco's, and every one from before kept.
            string[] lost = t2.Pairs.Except(want.Pairs).ToArray(), added = want.Pairs.Except(t2.Pairs).ToArray();
            Require.That(lost.Length == 0, "coupling the loco kept every coupling", $"lost {string.Join(" ", lost)}: {want.Topo}");
            Require.That(added.Length == 1 && added[0].Split('-').Contains(l.LocoNetId.ToString()), "coupling the loco added one coupling, to the loco",
                $"added '{string.Join(" ", added)}', the loco is {l.LocoNetId}: {want.Topo}");
            Report.Note(want.Topo);

            Boarded r = await pair.Host.Run<Boarded>(s => InGame.ReloadAndBoard(s, l.Loco, o.Reloads));
            Report.Note($"interior loaded: {r.Loaded}, host on board: {r.Onboard}");
            Require.That(r.Onboard, $"the host boarded {l.Loco}");
            // The client watches from beside the train (its own followers and interest).
            await pair.Client.Run(() => InGame.StandBeside(l.Loco));

            for (int s = 0; s < o.Hold; s += 10)
            {
                await Task.Delay(10_000);   // the hold is what's tested: the coupling mustn't change during it
                Topology now = await Both(pair, all);
                Require.Equal(want.Topo, now.Topo, $"the train's coupling after {s + 10} s");
            }
            Report.Note($"unchanged on both: {want.Couplings} coupling(s) in {want.Sets} trainset(s)");

            string[] dropped = (await pair.Host.LogsSince(cursor))
                .Where(line => line.Message.Contains("sent to the host's own client"))
                .Select(line => DroppedTrainPacket.Match(line.Message))
                .Where(m => m.Success && Regex.IsMatch(m.Groups[1].Value, "Train|Topology|Coupl|Car"))
                .Select(m => m.Groups[1].Value).Distinct().ToArray();
            Expect.Empty(dropped, "train packets the server sent the host's own client");
        }

        // How the cars are coupled on each game: they must agree and have every car. The cars needn't
        // be one train (a shunting job's may stand apart): the checks are that the coupling stays.
        private static async Task<Topology> Both(Pair pair, ushort[] cars)
        {
            PeerResults<Topology> r = await pair.Both.Run(() => InGame.Topology(cars));
            Require.Zero(r.Host.Missing, $"cars the host is missing ({r.Host.Topo})");
            Require.Zero(r.Client.Missing, $"cars the client is missing ({r.Client.Topo})");
            Require.Equal(r.Host.Topo, r.Client.Topo, "the client couples the train as the host does");
            return r.Host;
        }

        public sealed class Generated
        {
            public string Job, Type, Rounds;
            public ushort[] Cars;
        }

        public sealed class Coupled
        {
            public string Loco, End, How;
            public ushort LocoNetId;
            public float Closest;
        }

        public sealed class Boarded
        {
            public bool Loaded, Onboard;
        }

        public sealed class Topology
        {
            /// <summary>For each car (by net ID), the net IDs of the cars on its front and rear couplers
            /// (0: none).</summary>
            public string Topo;
            public int Sets, Couplings, Missing;
            /// <summary>Each coupling once, as "lower-higher" net IDs; one to a car outside the list too.</summary>
            public string[] Pairs;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// <summary>Host: clears payable debts and expires the station's offered jobs. Result: the IDs
            /// of its jobs now, offered and taken (none of them is fresh).</summary>
            [Step]
            public static string[] Expire(string station)
            {
                Jobs.ClearDebts();
                var st = StationController.allStations.First(s => s.stationInfo.YardID == station);
                string[] old = st.logicStation.availableJobs.Concat(st.logicStation.takenJobs).Select(j => j.ID).ToArray();
                st.ExpireAllAvailableJobsInStation();
                return old;
            }

            /// <summary>
            /// Host: a fresh job, generated as the players arrived (none of <paramref name="old"/>), or
            /// else made by the station's job generator: its available jobs are expired, then
            /// StationProceduralJobsController generates new ones, up to three rounds. Picks a new job with
            /// cars, whose overview is printed and which the career is licensed for: a transport or
            /// empty-haul one if there is one, else any (a shunting job's cars are a job train too).
            /// </summary>
            [Step]
            public static IEnumerator Generate(Step<Generated> step, string station, string[] old)
            {
                Jobs.ClearDebts();
                var st = StationController.allStations.First(s => s.stationInfo.YardID == station);
                var seen = new HashSet<Job>(st.logicStation.availableJobs.Concat(st.logicStation.takenJobs).Where(j => old.Contains(j.ID)));
                bool Licensed(Job j) => LicenseManager.Instance.IsLicensedForJob(DV.ThingTypes.JobLicenseType_v2.ToV2List(j.requiredLicenses));
                bool Preferred(Job j) => j.jobType == DV.ThingTypes.JobType.Transport || j.jobType == DV.ThingTypes.JobType.EmptyHaul;
                // Distinct: a shunting job lists its cars once per task.
                List<ushort> Cars(Job j) => NetworkedJob.TryGetFromJob(j, out NetworkedJob n) ? JobData.FromJob(n).GetCars().Distinct().ToList() : new List<ushort>();
                bool Ready(Job j) => !seen.Contains(j) && Licensed(j) &&
                    NetworkedJob.TryGetFromJob(j, out NetworkedJob n) && n.JobOverview != null && n.JobOverview.GetItemState() == ItemState.Dropped &&
                    Cars(j).Count > 0;
                Job job = null;
                var rounds = new List<string>();
                for (int round = 0; job == null && round <= 3; round++)
                {
                    if (round > 0)   // round 0: the jobs generated as the players arrived
                    {
                        st.ExpireAllAvailableJobsInStation();
                        yield return new WaitForSeconds(1f);
                        st.ProceduralJobsController.TryToGenerateJobs();
                    }
                    float idleSince = -1f;
                    for (float t0 = Time.realtimeSinceStartup; job == null && Time.realtimeSinceStartup - t0 < 60f;)
                    {
                        yield return new WaitForSeconds(0.5f);
                        var ready = st.logicStation.availableJobs.Where(Ready).ToList();
                        job = ready.FirstOrDefault(Preferred);
                        // Once generation has finished and the overviews have had time to print, take any.
                        if (st.ProceduralJobsController.IsJobGenerationActive) idleSince = -1f;
                        else if (idleSince < 0f) idleSince = Time.realtimeSinceStartup;
                        if (job == null && idleSince >= 0f && Time.realtimeSinceStartup - idleSince > 4f)
                            job = ready.FirstOrDefault();
                    }
                    var fresh = st.logicStation.availableJobs.Where(j => !seen.Contains(j)).ToList();
                    rounds.Add((round == 0 ? "on arrival: " : $"round {round}: ") + (fresh.Count == 0 ? "no new jobs" :
                        string.Join(", ", fresh.Select(j => j.jobType + (Licensed(j) ? "" : " (not licensed)") + (Cars(j).Count == 0 ? " (no cars)" : "")))));
                    foreach (Job j in fresh) seen.Add(j);
                }
                if (job == null)
                    throw new Exception($"{station} generated no job to take: " + string.Join("; ", rounds));
                List<ushort> cars = Cars(job);
                // Let the cars settle and the job reach the client.
                yield return new WaitForSeconds(3f);
                step.Result = new Generated { Job = job.ID, Type = job.jobType.ToString(), Cars = cars.ToArray(), Rounds = string.Join("; ", rounds) };
            }

            /// <summary>
            /// Host: a shunter on the job's track rolls onto a free end of the job's train and the host
            /// couples it the way a player does when the couplers meet (the coupling helper). The loco is
            /// put on the end car's own track, 2.5 m (then 6 m) past its free coupler, where vanilla's
            /// spawner finds room (CarSpawner.IsBoxOverlapping), and rolls that gap. Spawned "near" the
            /// end instead (SpawnCarOnClosestTrack), it took a neighbouring yard track's or the nearest
            /// free spot, 70-180 m away, and stalled against the yard's other cars. An end with the
            /// track ending, or no room, past it is skipped; a try that couples to something else or
            /// stops closing in is deleted and the next tried.
            /// </summary>
            [Step]
            public static IEnumerator CoupleLoco(Step<Coupled> step, ushort[] cars, string locoLivery)
            {
                var livery = DV.Globals.G.Types.Liveries.First(l => l.id == locoLivery);
                var jobCars = new List<TrainCar>();
                foreach (ushort id in cars.Distinct())
                    if (NetworkedTrainCar.TryGet(id, out TrainCar c) && c != null)
                        jobCars.Add(c);
                var ends = new List<(TrainCar car, Coupler coupler)>();
                foreach (TrainCar car in jobCars)
                    foreach (Coupler cp in new[] { car.frontCoupler, car.rearCoupler })
                        if (!cp.IsCoupled())
                            ends.Add((car, cp));
                var tried = new List<string>();
                foreach (var (endCar, end) in ends)
                foreach (float distance in new[] { 2.5f, 6f })
                {
                    // The end car's track at its free coupler, and which way along its points is outward.
                    Bogie bogie = endCar.Bogies.OrderBy(b => Vector3.Distance(b.transform.position, end.transform.position)).First();
                    RailTrack track = bogie.track;
                    if (track == null) { tried.Add($"{endCar.ID}: derailed"); continue; }
                    var points = track.GetKinkedPointSet().points;
                    Vector3 At(int k) => (Vector3)points[k].position + WorldMover.currentMove;
                    // Exact spans along the track (points can be metres apart, so whole-point steps from the
                    // point nearest the coupler could start under the end car and put the loco on it).
                    double SpanOf(Vector3 world)
                    {
                        double best = 0; float bestSqr = float.MaxValue;
                        for (int k = 0; k + 1 < points.Length; k++)
                        {
                            Vector3 a = At(k), d = At(k + 1) - a;
                            float t = d.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector3.Dot(world - a, d) / d.sqrMagnitude);
                            float sqr = (a + d * t - world).sqrMagnitude;
                            if (sqr < bestSqr) { bestSqr = sqr; best = points[k].span + t * (points[k + 1].span - points[k].span); }
                        }
                        return best;
                    }
                    (Vector3 position, Vector3 forward) AtSpan(double s)
                    {
                        int k = 0;
                        while (k + 2 < points.Length && points[k + 1].span < s) k++;
                        double length = points[k + 1].span - points[k].span;
                        float t = length <= 0 ? 0f : (float)((s - points[k].span) / length);
                        return (Vector3.Lerp(At(k), At(k + 1), t), (At(k + 1) - At(k)).normalized);
                    }
                    double couplerSpan = SpanOf(end.transform.position);
                    int outward = couplerSpan >= SpanOf(endCar.transform.position) ? 1 : -1;
                    // The loco's centre: its half length plus the gap past the coupler, along the track.
                    Bounds bounds = CarSpawner.GetBoundsOfCar(livery.prefab);
                    double centre = couplerSpan + outward * (bounds.extents.z + distance);
                    double trackEnd = points[points.Length - 1].span;
                    if (centre - bounds.extents.z < 0 || centre + bounds.extents.z > trackEnd)
                    {
                        tried.Add($"{endCar.ID} +{distance} m: {track.name} ends {(outward > 0 ? trackEnd - couplerSpan : couplerSpan):F0} m past it");
                        step.Note(tried.Last());
                        continue;
                    }
                    (Vector3 position, Vector3 forward) = AtSpan(centre);
                    // What the spawner's box hits, raised the way IsBoxOverlapping raises it.
                    TrainCar[] Blocking()
                    {
                        Vector3 half = bounds.extents;
                        float lift = Mathf.Max(0f, 1.5f - half.y);
                        half.y += lift;
                        Vector3 centreOfBox = position + Vector3.up * (bounds.extents.y + lift + 0.15f);
                        return Physics.OverlapBox(centreOfBox, half, Quaternion.LookRotation(forward), CarSpawner.TrainCheckLayerMask, QueryTriggerInteraction.Ignore)
                            .Select(c => c.GetComponentInParent<TrainCar>()).Where(c => c != null).Distinct().ToArray();
                    }
                    // A full yard parks other consists right behind the job's train: those that hold no
                    // job car are cleared out of the way (the run's own cleanup doesn't need them).
                    TrainCar[] blocking = Blocking();
                    var strangers = blocking.Where(c => !jobCars.Contains(c) && !c.trainset.cars.Any(jobCars.Contains))
                        .SelectMany(c => c.trainset.cars).Distinct().ToList();
                    if (blocking.Length > 0 && strangers.Count > 0 && blocking.All(c => strangers.Contains(c)))
                    {
                        step.Note($"{endCar.ID} +{distance} m: clearing {strangers.Count} car(s) parked past it ({string.Join(",", strangers.Select(c => c.ID))})");
                        foreach (TrainCar stranger in strangers) Trains.Delete(stranger);
                        for (int i = 0; i < 30; i++) yield return null;
                    }
                    if (CarSpawner.IsBoxOverlapping(position, bounds.extents, Quaternion.LookRotation(forward)))
                    {
                        string hits = string.Join(",", Blocking().Select(c => c.ID));
                        tried.Add($"{endCar.ID} +{distance} m: no room on {track.name} (hits {hits})");
                        step.Note(tried.Last());
                        continue;
                    }
                    TrainCar loco = CarSpawner.Instance.SpawnCar(livery.prefab, track, position, forward, true, false);
                    if (loco == null) { tried.Add($"{endCar.ID} +{distance} m: not spawned"); continue; }
                    Cars.Spawned(loco);
                    for (int i = 0; i < 60; i++) yield return null;
                    if (loco.trainset.cars.Any(c => c != loco && c != endCar && !jobCars.Contains(c)))
                    {
                        tried.Add($"{endCar.ID} +{distance} m: coupled to {string.Join(",", loco.trainset.cars.Where(c => c != loco).Select(c => c.ID))}");
                        step.Note(tried.Last());
                        Trains.Delete(loco);
                        for (int i = 0; i < 30; i++) yield return null;
                        continue;
                    }

                    Coupler near = new[] { loco.frontCoupler, loco.rearCoupler }
                        .OrderBy(c => Vector3.Distance(c.transform.position, end.transform.position)).First();
                    float start = Vector3.Distance(near.transform.position, end.transform.position);
                    foreach (TrainCar car in jobCars) car.brakeSystem.SetHandbrakePosition(1f);
                    loco.brakeSystem.SetHandbrakePosition(0f);
                    float closest = float.MaxValue;
                    string how = "none";
                    float closer = Time.time;   // when the gap last shrank (by 0.1 m)
                    float fastest = 0f;
                    for (float t0 = Time.time; Time.time - t0 < 60f;)
                    {
                        yield return new WaitForFixedUpdate();
                        fastest = Mathf.Max(fastest, loco.rb.velocity.magnitude);   // after physics moved it
                        float gap = Vector3.Distance(near.transform.position, end.transform.position);
                        if (gap < closest - 0.1f) closer = Time.time;
                        closest = Mathf.Min(closest, gap);
                        if (near.coupledTo == end) { how = "on contact"; break; }
                        // Stopped short (buffers against something): the next try, while there's time for it.
                        if (Time.time - closer > 10f) { how = "stopped closing in"; break; }
                        if (gap < 0.9f)
                        {
                            new DV.HUD.UICouplingHelper { trainCar = endCar, shouldAutoHandbrake = () => false }.HandleCoupling(end, false);
                            how = "coupling helper";
                            break;
                        }
                        Vector3 toward = Vector3.ProjectOnPlane(end.transform.position - near.transform.position, Vector3.up);
                        Vector3 dir = loco.transform.forward * Mathf.Sign(Vector3.Dot(loco.transform.forward, toward));
                        float v = gap > 5f ? 2f : 0.6f;
                        loco.rb.AddForce(dir * v - Vector3.Project(loco.rb.velocity, dir), ForceMode.VelocityChange);
                    }
                    for (int i = 0; i < 50; i++) { yield return new WaitForFixedUpdate(); loco.rb.velocity = Vector3.zero; }
                    loco.brakeSystem.SetHandbrakePosition(1f);
                    for (int i = 0; i < 60; i++) yield return null;
                    if (near.coupledTo == end)
                    {
                        NetworkedTrainCar.TryGetNetId(loco, out ushort locoNetId);
                        step.Result = new Coupled { Loco = loco.ID, LocoNetId = locoNetId, End = endCar.ID, How = how, Closest = (float)Math.Round(closest, 2) };
                        yield break;
                    }
                    tried.Add($"{endCar.ID} +{distance} m: rolled from {start:F1} to {closest:F2} m (fastest {fastest:F1} m/s{(loco.rb.isKinematic ? ", kinematic" : "")}), not coupled ({how})");
                    step.Note(tried.Last());
                    Trains.Delete(loco);
                    for (int i = 0; i < 30; i++) yield return null;
                }
                throw new Exception("couldn't couple a " + livery.id + " to the job's train: " + string.Join("; ", tried));
            }

            /// <summary>Either peer: how the given cars are coupled.</summary>
            [Step]
            public static Topology Topology(ushort[] cars)
            {
                string Neighbour(Coupler c) =>
                    c != null && c.coupledTo != null && c.coupledTo.train != null && NetworkedTrainCar.TryGetNetId(c.coupledTo.train, out ushort n) ? n.ToString() : "0";
                var parts = new List<string>();
                var sets = new HashSet<Trainset>();
                var pairs = new HashSet<string>();
                int missing = 0;
                foreach (ushort id in cars.Distinct().OrderBy(id => id))
                {
                    if (!NetworkedTrainCar.TryGet(id, out TrainCar car) || car == null)
                    {
                        missing++;
                        parts.Add(id + ":missing");
                        continue;
                    }
                    if (car.trainset != null)
                        sets.Add(car.trainset);
                    string front = Neighbour(car.frontCoupler), rear = Neighbour(car.rearCoupler);
                    parts.Add($"{id}:{front}/{rear}");
                    foreach (string other in new[] { front, rear }.Where(n => n != "0"))
                    {
                        ushort o = ushort.Parse(other);
                        pairs.Add(Math.Min(id, o) + "-" + Math.Max(id, o));
                    }
                }
                return new Topology { Topo = string.Join(" ", parts), Sets = sets.Count, Couplings = pairs.Count, Missing = missing, Pairs = pairs.OrderBy(p => p).ToArray() };
            }

            /// <summary>Host: loads the loco's cab <paramref name="reloads"/> times from beside it, then
            /// boards it.</summary>
            [Step]
            public static IEnumerator ReloadAndBoard(Step<Boarded> step, string loco, int reloads)
            {
                var car = Trains.Find(loco);
                Vector3 side = Dev.Abs(car.transform.position + car.transform.right * 4f);
                yield return LocalPlayer.TeleportToGround(side.x, side.z);
                for (float t0 = Time.realtimeSinceStartup; !car.IsInteriorLoaded && Time.realtimeSinceStartup - t0 < 15f;) yield return null;
                for (int i = 0; i < reloads; i++)
                {
                    car.UnloadInterior();
                    yield return null;
                    car.LoadInterior();
                    yield return new WaitForSecondsRealtime(1.5f);
                }
                yield return Trains.Board(car);
                yield return new WaitForSeconds(3);
                step.Result = new Boarded { Loaded = car.IsInteriorLoaded, Onboard = PlayerManager.Car == car };
            }

            /// <summary>Host: gives a taken job back, as abandoning it at the career manager does.</summary>
            [Step]
            public static void Abandon(string jobId)
            {
                var job = DV.Logic.Job.JobsManager.Instance.currentJobs.FirstOrDefault(j => j.ID == jobId);
                if (job != null) DV.Logic.Job.JobsManager.Instance.AbandonJob(job);
            }

            /// <summary>The client: onto the ground 6 m to the loco's other side.</summary>
            [Step]
            public static IEnumerator StandBeside(string loco)
            {
                var car = Trains.Find(loco);
                Vector3 side = Dev.Abs(car.transform.position - car.transform.right * 6f);
                yield return LocalPlayer.TeleportToGround(side.x, side.z);
            }
        }
    }
}
