using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Pile-up: on K parallel tracks of Steel Mill's B yard, a loco runs into a row of N free wagons
    /// (from both ends with ENDS=2), all at once, so cars hit each other in chains. When everything
    /// has stopped on both peers, every car must agree between host and client: same derailed state,
    /// same coupled neighbours, and the same place. It doesn't judge what the crash should look like,
    /// only that both games saw the same one. MIN_DERAILED=N makes it a derailment test: it fails as
    /// invalid unless the host derails at least N cars (SPEED=25 K=2 N=4 MIN_DERAILED=1 derails
    /// reliably), and derailed cars must also face the same way on both peers (within 10 degrees)
    /// and hold still on the client once stopped. Deletes whatever was on the tracks it uses.
    /// </summary>
    [Scenario("pileup", "Locos rammed into rows of free wagons on parallel yard tracks: once everything stops, every car matches on both peers (derailed, coupled neighbours, place, a derailed car's facing)")]
    [Budget(900)]
    public sealed class Pileup : Scenario<Pileup.Options>
    {
        public sealed class Options
        {
            /// <summary>Tracks.</summary>
            public int K = 4;
            /// <summary>Wagons per track.</summary>
            public int N = 6;
            /// <summary>m/s.</summary>
            public float Speed = 8;
            /// <summary>Metres between wagons.</summary>
            public float Gap = 3;
            /// <summary>1: a loco at one end of each row; 2: at both.</summary>
            public int Ends = 1;
            /// <summary>0: derailing off, as the difficulty option.</summary>
            public bool Derail = true;
            /// <summary>The client's receive lag, ms.</summary>
            public int Lag;
            /// <summary>Fewer cars derailed on the host than this fails the run as invalid.</summary>
            public int MinDerailed;
            /// <summary>Seconds of cars still moving before the host sets every handbrake.</summary>
            public int BrakeAfter = 10;
            /// <summary>Stand the players either side of the first impact (to watch with dvmp up --visible).</summary>
            public bool Watch;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            // Both players to Steel Mill. A dedicated host has no player: the server keeps the track
            // there loaded (ServerWorldCoverage) without one.
            if (!pair.Dedicated) await pair.Host.Run(() => Players.TeleportToGround(7927f, 7338f), 90);
            await pair.Client.Run(() => InGame.GoEmptyHanded(7932f, 7342f), 90);
            await Task.Delay(10000);   // the area streams in on both before spawning (a spawn mid-stream misplaces cars)

            using (await pair.Client.Lag(o.Lag))
            {
                if (!o.Derail)
                {
                    // Derailing off is the difficulty option's effect: an unreachable derail stress threshold.
                    float original = await pair.Host.Run(() => InGame.DerailThreshold());
                    pair.Cleanup(() => Task.WhenAll(pair.Host.Run(() => InGame.SetDerailThreshold(original)), pair.Client.Run(() => InGame.SetDerailThreshold(original))));
                    await Task.WhenAll(pair.Host.Run(() => InGame.SetDerailThreshold(float.MaxValue)), pair.Client.Run(() => InGame.SetDerailThreshold(float.MaxValue)));
                }

                Rows rows = await pair.Host.Run<Rows>(s => InGame.Setup(s, o.K, o.N, o.Gap, 25f, o.Ends, "[Y]_[SM]_[B-", "BoxcarBrown"), 120);
                pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, rows.Cars)));
                Report.Note($"{rows.Cars.Length} cars on {o.K} tracks: " + string.Join(" ", rows.Tracks.Select(t => t.Replace("[Y]_[SM]_", ""))));
                await Task.Delay(3000);

                if (o.Watch)
                {
                    // The host 20 m to one side of the first impact and the client 20 m to the other, both facing it.
                    if (!pair.Dedicated) await pair.Host.Run(() => InGame.Face(rows.FirstWagon, 1f), 90);
                    await pair.Client.Run(() => InGame.Face(rows.FirstWagon, -1f), 90);
                    await Task.Delay(5000);
                }

                Rammed r = await pair.Host.Run<Rammed>(s => InGame.Ram(s, rows.Cars, rows.Rams, o.Speed), 90);
                Report.Note($"{o.Speed} m/s into every row at once (ends: {o.Ends}, derailing {(o.Derail ? "on" : "off")}, lag {o.Lag} ms): " +
                            $"hit at {r.HitSpeeds} m/s; fastest car {r.MaxSpeed} m/s; {r.Derailed} derailed, {r.Coupled} coupled (host)");

                // Until every car has stopped on both peers, twice in a row. Cars a hard crash sends
                // rolling down the yard would take minutes: after BRAKE_AFTER seconds the host puts
                // every handbrake on, as a crew catching runaways would. That stops them on the host;
                // clients must then stop where it did.
                PeerResults<CarState[]> states = null;
                int still = 0, moving = 0;
                bool braked = false;
                string movingNote = "";
                for (int i = 1; i <= 40; i++)
                {
                    await Task.Delay(2000);
                    CarState[] h = await pair.Host.Run(() => InGame.State(rows.Cars));
                    CarState[] c = await pair.Client.Run(() => InGame.State(rows.Cars));
                    states = new PeerResults<CarState[]>(h, c);
                    var counts = states.Select(p => (p.Peer, cars: p.Value.Where(x => x != null && x.Speed > 0.05f).ToList())).ToList();
                    moving = counts.Sum(x => x.cars.Count);
                    movingNote = string.Join(", ", counts.Select(x => x.Peer + " " + x.cars.Count + (x.cars.Count > 0 ? $" (up to {x.cars.Max(y => y.Speed):0.00} m/s)" : "")));
                    if (moving == 0) { if (++still >= 2) break; }
                    else still = 0;
                    if (!braked && i * 2 >= o.BrakeAfter)
                    {
                        Report.Note($"still moving after {o.BrakeAfter} s: {movingNote}; the host applies every handbrake");
                        await pair.Host.Run(() => InGame.Brake(rows.Cars));
                        braked = true;
                    }
                }
                if (moving != 0) Report.Note($"warning: still moving after 80 s: {movingNote}");
                // A settled derailed car must hold still on the client too: corrections fighting its
                // pose show as jitter. The client is read again 5 s later.
                await Task.Delay(5000);
                CarState[] later = await pair.Client.Run(() => InGame.State(rows.Cars));
                Judge(states, later, o.MinDerailed);
            }
        }

        private static void Judge(PeerResults<CarState[]> states, CarState[] later, int minDerailed)
        {
            var missing = new List<string>();
            var derailedDiffer = new List<string>();
            var coupledDiffer = new List<string>();
            var apart = new List<string>();
            var facing = new List<string>();
            var jitter = new List<string>();
            var skipped = new List<string>();
            (string id, double d) worst = ("", 0), worstTurn = ("", 0), worstJitter = ("", 0);
            int derailed = 0;
            for (int i = 0; i < states.Host.Length; i++)
            {
                CarState a = states.Host[i], b = states.Client[i], b2 = later[i];
                if (a.Missing || b.Missing) { missing.Add(a.Id + (a.Missing ? ": missing on host" : ": missing on client")); continue; }
                // A car still moving on the host is not where it will stop; reading host then client
                // gives a gap that is not a desync. Only cars the host has stopped are compared.
                if (a.Speed > 0.05f) { skipped.Add($"{a.Id} ({a.Speed:0.00} m/s)"); continue; }
                if (a.Derailed) derailed++;
                if (a.Derailed != b.Derailed) derailedDiffer.Add($"{a.Id}: host {a.Derailed}, client {b.Derailed}");
                if (a.Coupled != b.Coupled) coupledDiffer.Add($"{a.Id}: host [{a.Coupled}], client [{b.Coupled}]");
                double d = Distance(a, b), limit = a.Derailed ? 1.0 : 0.25;
                if (d > worst.d) worst = (a.Id, d);
                if (d > limit) apart.Add($"{a.Id}: {d:0.00} m{(a.Derailed ? " (derailed)" : "")}");
                if (a.Derailed && b.Derailed)
                {
                    double turn = Math.Max(Angle(a.F, b.F), Angle(a.U, b.U));
                    if (!(turn >= 0)) { facing.Add(a.Id + ": no facing to compare"); continue; }
                    if (turn >= worstTurn.d) worstTurn = (a.Id, turn);
                    if (turn > 10) facing.Add($"{a.Id}: {turn:0.0} degrees apart");
                    if (!b2.Missing)
                    {
                        double moved = Distance(b, b2), turned = Math.Max(Angle(b.F, b2.F), Angle(b.U, b2.U));
                        if (moved >= worstJitter.d) worstJitter = (a.Id, moved);
                        if (moved > 0.05 || turned > 2) jitter.Add($"{a.Id}: {moved:0.000} m, {turned:0.0} degrees in 5 s");
                    }
                }
            }
            if (derailed > 0)
                Report.Note($"derailed cars: facing within {worstTurn.d:0.0} degrees ({worstTurn.id}), client moved up to {worstJitter.d:0.000} m in the 5 s after stopping ({worstJitter.id})");
            Report.Note($"{states.Host.Length - skipped.Count} cars compared, {derailed} derailed; largest gap {worst.d:0.00} m ({worst.id})" +
                        (skipped.Count > 0 ? "; skipped, still moving on the host: " + string.Join(", ", skipped) : ""));
            Expect.Empty(missing, "cars missing on a peer");
            Expect.Empty(derailedDiffer, "cars derailed on one peer only");
            Expect.Empty(coupledDiffer, "cars coupled to different neighbours");
            Expect.Empty(apart, "cars apart beyond 0.25 m (1 m derailed)");
            Expect.Empty(facing, "derailed cars facing over 10 degrees apart");
            Expect.Empty(jitter, "derailed cars still moving on the client after they stopped on the host (over 0.05 m or 2 degrees in 5 s)");
            Expect.AtLeast(derailed, minDerailed, "cars derailed on the host (MIN_DERAILED: fewer is nothing to test, raise SPEED)");
        }

        private static double Distance(CarState a, CarState b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

        private static double Angle(float[] a, float[] b)
        {
            double dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
            double n = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]) * Math.Sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2]);
            return Math.Acos(Math.Max(-1, Math.Min(1, dot / n))) * 180 / Math.PI;
        }

        public sealed class Rows
        {
            public string[] Cars, Tracks;
            /// <summary>"loco:target" per ramming loco.</summary>
            public string[] Rams;
            public string FirstWagon;
        }

        public sealed class Rammed
        {
            public string HitSpeeds;
            public float MaxSpeed;
            public int Derailed, Coupled;
        }

        public sealed class CarState
        {
            public string Id;
            public bool Missing, Derailed;
            public double X, Y, Z;
            /// <summary>Its forward and up.</summary>
            public float[] F, U;
            public float Speed;
            /// <summary>The IDs it's coupled to, sorted, joined by +.</summary>
            public string Coupled;
        }

        // ---- runs inside the games
        public static class InGame
        {
            [Step]
            public static IEnumerator GoEmptyHanded(float x, float z)
            {
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.TeleportToGround(x, z);
            }

            [Step]
            public static float DerailThreshold() => DV.Globals.G.GameParams.DerailStressThreshold;

            [Step]
            public static void SetDerailThreshold(float threshold) => DV.Globals.G.GameParams.DerailStressThreshold = threshold;

            /// <summary>
            /// Host: clears <paramref name="k"/> parallel yard tracks (named <paramref name="prefix"/>*)
            /// and lays out, on each, a row of <paramref name="n"/> uncoupled wagons <paramref name="gap"/>
            /// metres apart with a loco <paramref name="runup"/> metres before it (and another after it
            /// with <paramref name="ends"/> 2). Handbrakes stay on until Ram releases them, so nothing
            /// rolls off on a grade first.
            /// </summary>
            [Step]
            public static IEnumerator Setup(Step<Rows> step, int k, int n, float gap, float runup, int ends, string prefix, string wagon)
            {
                var wagonLivery = Cars.Livery(wagon);
                var locoLivery = Cars.Livery("LocoDE2");
                const float WagonLength = 14.1f, LocoLength = 13f; // coupler to coupler, roughly
                float pitch = WagonLength + gap;
                float need = n * pitch + ends * (runup + LocoLength) + 20f;

                var tracks = UnityEngine.Object.FindObjectsOfType<RailTrack>()
                    .Where(t => t.name.StartsWith(prefix) && t.curve != null && t.curve.length >= need)
                    .OrderBy(t => t.name).Take(k).ToList();
                if (tracks.Count < k) throw new Exception($"only {tracks.Count} tracks named {prefix}* are at least {need:F0} m long");

                TrainCar Spawn(RailTrack t, DV.ThingTypes.TrainCarLivery livery, double span)
                {
                    // SpawnCarFromRemote places a car at an exact point and is synced to clients (plain SpawnCar isn't).
                    var p = t.GetKinkedPointSet().points.OrderBy(q => Math.Abs(q.span - span)).First();
                    var car = CarSpawner.Instance.SpawnCarFromRemote(livery.prefab, t, (Vector3)p.position, (Vector3)p.forward)
                              ?? throw new Exception($"failed to spawn {livery.id} on {t.name} at {span:F0}");
                    Cars.Record(new[] { car });
                    return car;
                }

                var all = new List<string>(); var rams = new List<string>(); string firstWagon = null;
                foreach (var t in tracks)
                {
                    CarSpawner.Instance.DeleteTrainCarsFromTrack(t);
                    // Clean room: the yard's job generator keeps putting cars on and next to these
                    // tracks, and clearing a track misses cars standing partly on the ladder. Rows
                    // spawned overlapping them blew apart (job cars logged "derailed at 13385 km/h"), so
                    // derailments came from that, not the crash. Delete anything within 3 m of the
                    // centreline (parallel tracks are 4.5 m away).
                    var path = t.GetKinkedPointSet().points.Where((p, i) => i % 5 == 0).Select(p => (Vector3)p.position + WorldMover.currentMove).ToList();
                    foreach (var other in UnityEngine.Object.FindObjectsOfType<TrainCar>().ToList())
                        if (other != null && path.Any(p => Vector3.Distance(p, other.transform.position) < 3f))
                            CarSpawner.Instance.DeleteCar(other);
                    for (int i = 0; i < 10; i++) yield return null;
                    double len = t.curve.length;
                    double first = (len - n * pitch) / 2 + pitch / 2;           // middle of the first wagon
                    var wagons = new List<TrainCar>();
                    for (int i = 0; i < n; i++) wagons.Add(Spawn(t, wagonLivery, first + i * pitch));
                    var locos = new List<(TrainCar loco, TrainCar target)> { (Spawn(t, locoLivery, first - pitch / 2 - runup - LocoLength / 2), wagons[0]) };
                    if (ends == 2)
                        locos.Add((Spawn(t, locoLivery, first + (n - 1) * pitch + pitch / 2 + runup + LocoLength / 2), wagons[n - 1]));
                    for (int i = 0; i < 30; i++) yield return null;
                    foreach (var car in wagons.Concat(locos.Select(x => x.loco)))
                    {
                        foreach (var cp in new[] { car.frontCoupler, car.rearCoupler })
                            if (cp.coupledTo != null) cp.Uncouple(false, false, false, false);
                        car.brakeSystem.SetHandbrakePosition(1f);
                        all.Add(car.ID);
                    }
                    foreach (var (loco, target) in locos) rams.Add(loco.ID + ":" + target.ID);
                    firstWagon ??= wagons[0].ID;
                }
                for (int i = 0; i < 60; i++) yield return new WaitForFixedUpdate();

                // Nothing may already be moving or off the rails before the run starts.
                var mine = all.Select(id => UnityEngine.Object.FindObjectsOfType<TrainCar>().FirstOrDefault(c => c.ID == id)).ToList();
                var bad = mine.Where(c => c == null || c.derailed || c.rb.velocity.magnitude > 0.2f).Select(c => c == null ? "missing" : $"{c.ID} {(c.derailed ? "derailed" : $"{c.rb.velocity.magnitude:F1} m/s")}").ToList();
                if (bad.Count > 0) throw new Exception("setup is not clean: " + string.Join(", ", bad));
                step.Result = new Rows { Cars = all.ToArray(), Rams = rams.ToArray(), Tracks = tracks.Select(t => t.name).ToArray(), FirstWagon = firstWagon };
            }

            /// <summary>This game's player 20 m to one <paramref name="side"/> of the car, facing it.</summary>
            [Step]
            public static IEnumerator Face(string id, float side)
            {
                var car = Trains.Find(id);
                Vector3 at = Dev.Abs(car.transform.position) + car.transform.right * 20f * side;
                yield return LocalPlayer.TeleportToGround(at.x, at.z);
                Vector3 look = Vector3.ProjectOnPlane(car.transform.position - PlayerManager.PlayerTransform.position, Vector3.up);
                PlayerManager.PlayerTransform.rotation = Quaternion.LookRotation(look);
            }

            /// <summary>Host: releases every handbrake and drives each loco into its row at
            /// <paramref name="speed"/> m/s, all at once, letting it coast from 3 m out. Then waits up
            /// to 30 s for everything to stop.</summary>
            [Step]
            public static IEnumerator Ram(Step<Rammed> step, string[] ids, string[] ramPairs, float speed)
            {
                var cars = ids.Select(Find).Where(x => x != null).ToList();
                var rams = ramPairs.Select(p => p.Split(':')).Select(p => (loco: Find(p[0]), target: Find(p[1])))
                    .Where(r => r.loco != null && r.target != null).ToList();
                foreach (var car in cars) car.brakeSystem.SetHandbrakePosition(0f);

                var driving = new HashSet<TrainCar>(rams.Select(r => r.loco));
                var hitSpeeds = new List<float>();
                for (float t0 = Time.time; Time.time - t0 < 30f && driving.Count > 0;)
                {
                    yield return new WaitForFixedUpdate();
                    foreach (var (loco, target) in rams)
                    {
                        if (!driving.Contains(loco)) continue;
                        float gap = Vector3.Distance(loco.transform.position, target.transform.position) - 13.5f;
                        if (gap < 3f || loco.derailed) { hitSpeeds.Add(Mathf.Abs(loco.GetForwardSpeed())); driving.Remove(loco); continue; }
                        Vector3 toward = (target.transform.position - loco.transform.position).normalized;
                        Vector3 dir = loco.transform.forward * Mathf.Sign(Vector3.Dot(loco.transform.forward, toward));
                        loco.rb.AddForce(dir * speed - Vector3.Project(loco.rb.velocity, dir), ForceMode.VelocityChange);
                    }
                }
                float maxSpeed = 0f;
                for (float t0 = Time.time; Time.time - t0 < 30f;)
                {
                    yield return new WaitForSeconds(0.5f);
                    float fastest = cars.Where(x => x != null).Select(x => x.derailed ? x.rb.velocity.magnitude : Mathf.Abs(x.GetForwardSpeed())).DefaultIfEmpty(0f).Max();
                    maxSpeed = Mathf.Max(maxSpeed, fastest);
                    if (fastest < 0.05f && Time.time - t0 > 3f) break;
                }
                step.Result = new Rammed
                {
                    HitSpeeds = string.Join(",", hitSpeeds.Select(s => s.ToString("F1"))),
                    MaxSpeed = (float)Math.Round(maxSpeed, 1),
                    Derailed = cars.Count(x => x != null && x.derailed),
                    Coupled = cars.Count(x => x != null && (x.frontCoupler.coupledTo != null || x.rearCoupler.coupledTo != null))
                };
            }

            /// <summary>Host: every listed car's handbrake on.</summary>
            [Step]
            public static void Brake(string[] ids)
            {
                foreach (var c in ids.Select(Find).Where(c => c != null)) c.brakeSystem.SetHandbrakePosition(1f);
            }

            /// <summary>Either peer: per car its absolute position, facing (forward and up), speed,
            /// derailed, and the IDs it's coupled to.</summary>
            [Step]
            public static CarState[] State(string[] ids) => ids.Select(id =>
            {
                var car = Find(id);
                if (car == null) return new CarState { Id = id, Missing = true };
                var p = Dev.Abs(car.transform.position);
                string Coupled(Coupler cp) => cp?.coupledTo?.train?.ID ?? "";
                float[] R(Vector3 v) => new[] { (float)Math.Round(v.x, 4), (float)Math.Round(v.y, 4), (float)Math.Round(v.z, 4) };
                return new CarState
                {
                    Id = id,
                    X = Math.Round(p.x, 3), Y = Math.Round(p.y, 3), Z = Math.Round(p.z, 3),
                    F = R(car.transform.forward), U = R(car.transform.up),
                    Speed = (float)Math.Round(car.derailed ? car.rb.velocity.magnitude : Mathf.Abs(car.GetForwardSpeed()), 3),
                    Derailed = car.derailed,
                    Coupled = string.Join("+", new[] { Coupled(car.frontCoupler), Coupled(car.rearCoupler) }.Where(x => x != "").OrderBy(x => x))
                };
            }).ToArray();

            private static TrainCar Find(string id) => UnityEngine.Object.FindObjectsOfType<TrainCar>().FirstOrDefault(c => c.ID == id);
        }
    }
}
