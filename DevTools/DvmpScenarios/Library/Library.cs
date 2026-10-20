// Steps every scenario can use, and the plain data they pass. Each [Step] runs inside a game:
// call it through pair.Host / pair.Client / pair.Both, never directly.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dvmp.Agent;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>A position free of the floating origin (Dev.Abs), comparable across games.</summary>
    public sealed class Abs
    {
        public float X, Y, Z;
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##}, {2:0.##})", X, Y, Z);
    }

    /// <summary>Comparing what two games report. Runs in the orchestrator: plain arithmetic.</summary>
    public static class Peers
    {
        public static float Distance(Abs a, Abs b) => (float)Math.Sqrt(Sq(a.X - b.X) + Sq(a.Y - b.Y) + Sq(a.Z - b.Z));

        /// <summary>Apart on the ground, ignoring height.</summary>
        public static float Horizontal(Abs a, Abs b) => (float)Math.Sqrt(Sq(a.X - b.X) + Sq(a.Z - b.Z));

        private static double Sq(double v) => v * v;
    }

    /// <summary>A train a scenario spawned: the loco first, then its wagons.</summary>
    public sealed class Train
    {
        public string Loco;
        public string[] Cars;
        public string Last;
        /// <summary>1 to push the loco forward into the clear track, -1 backward.</summary>
        public int Dir;
        public string Track;
        public float Clear, Charged, Cylinders, Seconds;
    }

    /// <summary>A loco held still (Cars.SpawnHeldLoco): the loco, then its tender if it has one.</summary>
    public sealed class HeldLoco
    {
        public string Loco;
        public string[] Cars;
        public Abs At;
    }

    public static class Cars
    {
        // Cars the library's spawn steps made in this game since the last TakeSpawned: the leak check's
        // list. The world spawns cars of its own while a run goes on (StationLocoSpawner puts locos
        // at a station a player comes near), so "cars that weren't there before" isn't a leak.
        private static readonly HashSet<string> spawned = new();

        /// <summary>Every car this game has, by ID.</summary>
        [Step]
        public static string[] All() => CarSpawner.Instance.AllCars.Where(c => c != null).Select(c => c.ID).ToArray();

        /// <summary>Host: the IDs of the cars spawn steps made since the last call, and forgets them.</summary>
        [Step]
        public static string[] TakeSpawned()
        {
            string[] ids = spawned.ToArray();
            spawned.Clear();
            return ids;
        }

        /// <summary>Which of these cars this game still has.</summary>
        [Step]
        public static string[] Existing(string[] ids) => CarSpawner.Instance.AllCars.Where(c => c != null && ids.Contains(c.ID)).Select(c => c.ID).ToArray();

        [Step]
        public static Abs Position(string car) => Positions.Of(Trains.Find(car).transform.position);

        /// <summary>Host: deletes the cars in one go. Result: the IDs still there 30 frames later
        /// (none, unless something kept one).</summary>
        [Step]
        public static IEnumerator Delete(Step<string[]> step, string[] ids)
        {
            foreach (TrainCar c in CarSpawner.Instance.AllCars.Where(c => c != null && ids.Contains(c.ID)).ToList())
                CarSpawner.Instance.DeleteCar(c);
            for (int i = 0; i < 30; i++) yield return null;
            step.Result = CarSpawner.Instance.AllCars.Where(c => c != null && ids.Contains(c.ID)).Select(c => c.ID).ToArray();
        }

        /// <summary>
        /// Host: a <paramref name="loco"/> and <paramref name="wagons"/> wagons near the client's
        /// player, coupled with hoses joined and cocks open, the loco started, every handbrake off
        /// and the brake pipe charged, on a stretch with at least <paramref name="clear"/> metres of
        /// clear track on one side. Clear means following the switches as they're set, up to the
        /// first other car, buffer stop or end of the line: a push into whatever was ahead wrecked
        /// the train and failed the run.
        /// </summary>
        [Step]
        public static IEnumerator SpawnTrain(Step<Train> step, string loco, string wagon, int wagons, float clear)
        {
            var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First();
            DV.ThingTypes.TrainCarLivery L(string id) => DV.Globals.G.Types.Liveries.First(l => l.id == id);
            var liveries = new List<DV.ThingTypes.TrainCarLivery> { L(loco) };
            for (int i = 0; i < wagons; i++) liveries.Add(L(wagon));

            var rejected = new List<string>();
            foreach (float d in new[] { 20f, 40f, 60f, 80f, 120f, 160f, 200f })
            foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
            {
                var t = CarSpawner.GetTrackClosestTo(client.transform.position + dir * d, 60f, out int _);
                if (t == null) continue;
                var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(liveries, liveries.Select(_ => false).ToList(), t, true, false);
                if (cars == null) continue;
                foreach (var c in cars) spawned.Add(c.ID);
                for (int i = 0; i < 60; i++) yield return null;

                // Room from the loco, less the train's own length (the wagons may be on the side we push to).
                var ours = new HashSet<TrainCar>(cars);
                var first = cars[0];
                float trainLength = Vector3.Distance(cars.First().transform.position, cars.Last().transform.position) + 20f;
                int sd = SpanDir(first);
                float ahead = FreeAhead(first.Bogies[0].track, first.Bogies[0].traveller.Span, sd, ours, clear + trainLength) - trainLength;
                float behind = FreeAhead(first.Bogies[0].track, first.Bogies[0].traveller.Span, -sd, ours, clear + trainLength) - trainLength;
                if (Mathf.Max(ahead, behind) < clear)
                {
                    rejected.Add($"{t.name}: {ahead:F0}/{behind:F0} m clear");
                    foreach (var c in cars) CarSpawner.Instance.DeleteCar(c);
                    for (int i = 0; i < 10; i++) yield return null;
                    continue;
                }
                int push = ahead >= behind ? 1 : -1;

                for (int k = 0; k < cars.Count - 1; k++)
                {
                    Coupler ca = null, cb = null;
                    foreach (var x in new[] { cars[k].frontCoupler, cars[k].rearCoupler })
                    foreach (var y in new[] { cars[k + 1].frontCoupler, cars[k + 1].rearCoupler })
                        if (ca == null || Vector3.Distance(x.transform.position, y.transform.position) < Vector3.Distance(ca.transform.position, cb.transform.position)) { ca = x; cb = y; }
                    if (ca.coupledTo == null) ca.CoupleTo(cb, false, false);
                    ca.ConnectAirHose(cb, false); ca.IsCockOpen = true; cb.IsCockOpen = true;
                }
                // Close the cocks at both ends of the train, or the open ends vent the brake pipe.
                foreach (var c in cars) foreach (var end in new[] { c.frontCoupler, c.rearCoupler }) if (end.coupledTo == null) end.IsCockOpen = false;
                foreach (var c in cars) c.brakeSystem.SetHandbrakePosition(0f);
                yield return Trains.StartLoco(cars[0]);
                // Cut in the loco's train brake valve (a spawned loco has it cut out), or nothing feeds the pipe.
                cars[0].brakeSystem.trainBrakeCutout = true;
                float t0 = Time.time;
                while (Time.time - t0 < 120f && cars.Any(c => c.brakeSystem.brakePipePressure < 4.9f || c.brakeSystem.brakeCylinderPressure > 1.2f)) yield return null;
                step.Result = new Train
                {
                    Loco = cars[0].ID, Cars = cars.Select(c => c.ID).ToArray(), Last = cars.Last().ID, Dir = push, Track = t.name,
                    Clear = Mathf.Round(push > 0 ? ahead : behind), Charged = cars.Min(c => c.brakeSystem.brakePipePressure),
                    Cylinders = cars.Max(c => c.brakeSystem.brakeCylinderPressure), Seconds = Mathf.Round(Time.time - t0)
                };
                yield break;
            }
            throw new Exception($"no track with {clear:F0} m clear near the client (tried {string.Join("; ", rejected)})");
        }

        /// <summary>
        /// Host: a loco near the host's player that stays put: a steam loco with its tender coupled,
        /// every car braked, on a spot where it doesn't roll. The S282's handbrake is on its tender:
        /// spawned alone, it rolled away on a grade and left a boarded client behind, out of reach of
        /// its controls (#159). The S282A has no handbrake at all, so a car without one gets its air
        /// brake: its main reservoir charged and its independent brake applied, through its port.
        /// With that, an S282A and tender still crept 15-18 m in a minute on some grades: each spot is
        /// watched for 5 s, and one the loco moves 0.3 m or more on is given up for one further out.
        /// If every spot creeps, the one it crept least on is kept.
        /// </summary>
        [Step]
        public static IEnumerator SpawnHeldLoco(Step<HeldLoco> step, string livery)
        {
            var tried = new List<string>();
            var braked = new List<string>();
            string bestBraked = "";
            void Brake(TrainCar c)
            {
                if (c.brakeSystem.hasHandbrake)
                {
                    c.brakeSystem.SetHandbrakePosition(1f);
                    braked.Add(c.ID + ": handbrake");
                    return;
                }
                var flow = c.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>()?.simulationFlow;
                var ind = flow?.fullPortIdToPort.Where(kv => kv.Key.StartsWith("indBrake") && kv.Value.type == LocoSim.Definitions.PortType.EXTERNAL_IN)
                    .Select(kv => kv.Value).FirstOrDefault();
                if (ind == null)
                {
                    braked.Add(c.ID + ": no brake to apply");
                    return;
                }
                c.brakeSystem.SetMainReservoirPressure(8f);
                ind.ExternalValueUpdate(1f);
                braked.Add(c.ID + ": independent brake (" + ind.id + "), main reservoir charged");
            }
            TrainCar best = null, bestTender = null;
            float bestMoved = float.MaxValue;
            foreach (float ahead in new[] { 25f, 45f, 70f, 100f })
            {
                braked.Clear();
                var car = Trains.SpawnWithTenderNearPlayer(livery, ahead);
                var tenderLivery = Trains.TenderOf(car.carLivery);
                TrainCar tender = tenderLivery == null ? null : UnityEngine.Object.FindObjectsOfType<TrainCar>()
                    .Where(c => c.carLivery == tenderLivery).OrderBy(c => (c.transform.position - car.transform.position).sqrMagnitude).FirstOrDefault();
                foreach (var c in new[] { car, tender }.Where(c => c != null))
                {
                    spawned.Add(c.ID);
                    Brake(c);
                }
                yield return Trains.CoupleTender(car, 1.5f);
                for (int i = 0; i < 120; i++) yield return null;
                Vector3 from = Dev.Abs(car.transform.position);
                yield return new WaitForSecondsRealtime(5f);   // watching it creep
                float moved = Vector3.Distance(Dev.Abs(car.transform.position), from);
                if (moved < 0.3f || moved < bestMoved)
                {
                    if (best != null)
                        foreach (var c in new[] { best, bestTender }.Where(c => c != null))
                            Trains.Delete(c);
                    best = car; bestTender = tender; bestMoved = moved; bestBraked = string.Join("; ", braked);
                    if (moved < 0.3f)
                        break;
                }
                else
                    foreach (var c in new[] { car, tender }.Where(c => c != null))
                        Trains.Delete(c);
                tried.Add($"{ahead} m ahead: moved {moved:F2} m in 5 s");
                for (int i = 0; i < 30; i++) yield return null;
            }
            if (tried.Count > 0) step.Note("spots tried (the loco crept): " + string.Join("; ", tried));
            step.Note("braked: " + bestBraked);
            step.Result = new HeldLoco
            {
                Loco = best.ID,
                Cars = new[] { best, bestTender }.Where(c => c != null).Select(c => c.ID).ToArray(),
                At = Positions.Of(best.transform.position)
            };
        }

        /// <summary>Host: pushes the loco's consist (ramp to <paramref name="speed"/> m/s, hold, ramp
        /// back to a stop), then sets every handbrake, as a driver would: unbraked cars roll away
        /// on the slightest grade.</summary>
        [Step]
        public static IEnumerator Push(string loco, float speed, float hold, float ramp)
        {
            TrainCar car = Trains.Find(loco);
            yield return Trains.Push(car, speed, hold, ramp);
            foreach (TrainCar c in car.trainset.cars) c.brakeSystem.SetHandbrakePosition(1f);
        }

        /// <summary>Waits until the loco is at <paramref name="speed"/> m/s or more, for at most
        /// <paramref name="seconds"/>. Result: its speed then.</summary>
        [Step]
        public static IEnumerator WaitForSpeed(Step<float> step, string loco, float speed, float seconds)
        {
            TrainCar l = Trains.Find(loco);
            for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < seconds && Mathf.Abs(l.GetForwardSpeed()) < speed;)
                yield return null;
            step.Result = Mathf.Abs(l.GetForwardSpeed());
        }

        /// <summary>Waits until the loco is below 0.05 m/s, for at most <paramref name="seconds"/>.
        /// Result: its speed then.</summary>
        [Step]
        public static IEnumerator WaitUntilStopped(Step<float> step, string loco, float seconds)
        {
            TrainCar l = Trains.Find(loco);
            for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < seconds && Mathf.Abs(l.GetForwardSpeed()) > 0.05f;)
                yield return null;
            step.Result = Mathf.Abs(l.GetForwardSpeed());
        }

        // Metres of clear track from a point on a track in a span direction (1 = towards the out end).
        private static float FreeAhead(RailTrack t, double span, int dir, HashSet<TrainCar> ours, float max)
        {
            float dist = 0f;
            for (int hop = 0; hop < 60 && t != null; hop++)
            {
                double nearest = double.MaxValue;
                foreach (var c in CarSpawner.Instance.AllCars)
                    if (c != null && !ours.Contains(c))
                        foreach (var b in c.Bogies)
                            if (b.track == t && b.traveller != null)
                            {
                                double d = (b.traveller.Span - span) * dir;
                                if (d >= 0 && d < nearest) nearest = d;
                            }
                if (nearest != double.MaxValue) return Mathf.Min(max, dist + (float)nearest);
                double length = t.GetKinkedPointSet().span;
                dist += (float)(dir > 0 ? length - span : span);
                if (dist >= max) return max;
                var next = dir > 0 ? t.GetOutBranch() : t.GetInBranch();
                if (next == null || next.track == null) return dist; // buffer stop or end of the line
                t = next.track;
                dir = next.first ? 1 : -1;
                span = next.first ? 0 : t.GetKinkedPointSet().span;
            }
            return dist;
        }

        // Which span direction the car's forward points along its track.
        private static int SpanDir(TrainCar car)
        {
            var b = car.Bogies[0];
            var pts = b.track.GetKinkedPointSet().points.ToList();
            double s = b.traveller.Span;
            int i = Mathf.Clamp(pts.TakeWhile(p => p.span < s).Count(), 1, pts.Count - 1);
            Vector3 tangent = (Vector3)pts[i].position - (Vector3)pts[i - 1].position;
            return Vector3.Dot(car.transform.forward, tangent) >= 0 ? 1 : -1;
        }
    }

    public static class Players
    {
        /// <summary>Puts this game's player on the car's floor, attached the way the game attaches a
        /// player who steps on: to the car's interior (#171). The only boarding path.</summary>
        [Step]
        public static IEnumerator Board(string car) => Trains.Board(Trains.Find(car));

        [Step]
        public static IEnumerator TeleportToGround(float x, float z) => LocalPlayer.TeleportToGround(x, z);
    }

    public static class Net
    {
        /// <summary>Delays every packet this game receives (Steam's fake lag); 0 turns it off.</summary>
        [Step]
        public static void SetReceiveLag(int ms) => Steamworks.SteamNetworkingUtils.FakeRecvPacketLag = ms;
    }

    /// <summary>In-game conversions to the plain data steps return.</summary>
    internal static class Positions
    {
        public static Abs Of(Vector3 world)
        {
            Vector3 a = Dev.Abs(world);
            return new Abs { X = (float)Math.Round(a.x, 2), Y = (float)Math.Round(a.y, 2), Z = (float)Math.Round(a.z, 2) };
        }
    }
}
