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
    /// Host frame-rate collapse (Discord: "host at 2 fps for a minute caused hoses to break and
    /// couplings to show weird positions", "brake pipes randomly split, draining the MR and applying
    /// all the wagon brakes"). The host pushes a loco and wagons with a charged brake pipe, stalls at
    /// about 2 fps for a while, and recovers. Only breakage fails: neither peer may lose a coupling,
    /// hose or cock or see the brake pipe drop, and they must agree where the train stops. With the
    /// host in trouble a moving train's cars on the client snap, during the stall and after it
    /// (trains slow down to avoid rubber-banding), so the client's jumps are notes, split into
    /// before, during and after the stall.
    /// </summary>
    [Scenario("host-stall", "The host stalls at about 2 fps with a train at speed: no coupling, hose or cock is lost, the brake pipe holds, and the peers agree where it stops")]
    [Budget(600)]
    public sealed class HostStall : Scenario<HostStall.Options>
    {
        public sealed class Options
        {
            /// <summary>Seconds at about 2 fps.</summary>
            public int Stall = 30;
            /// <summary>m/s.</summary>
            public float Speed = 8;
            public int Wagons = 4;
            /// <summary>The client in the loco's cab (1) or standing by the line (0).</summary>
            public bool Ride = true;
            /// <summary>The client's receive lag, ms.</summary>
            public int Lag;
            /// <summary>Game seconds at speed.</summary>
            public int Hold = 15;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            // The push ramps up over 4 s, holds and ramps down over 4 s, plus a margin.
            float clear = o.Speed * (o.Hold + 4) + 60;
            Train train = await pair.Host.Run<Train>(s => Cars.SpawnTrain(s, "LocoDE2", "BoxcarBrown", o.Wagons, clear), 150);
            pair.Cleanup(async () =>
            {
                // A train left behind fouls the next run's track, and the save-writing runs after this one would save it.
                string[] left = await pair.Host.Run<string[]>(s => Cars.Delete(s, train.Cars));
                if (left.Length > 0) Report.Note("train not cleaned up: " + string.Join(",", left));
            });
            Report.Note($"{string.Join(",", train.Cars)} on {train.Track}, {train.Clear} m clear: brake pipe charged to {train.Charged:0.##} bar, " +
                        $"cylinders at {train.Cylinders:0.##} bar after {train.Seconds}s");
            if (o.Ride) await pair.Client.Run(() => Players.Board(train.Loco));

            int watch = o.Stall + 120;
            Watched hostWatch, clientWatch;
            Tracked track;
            Stalled stall;
            using (await pair.Client.Lag(o.Lag))
            {
                var watchers = pair.Both.Start<Watched>(s => InGame.Watch(s, train.Cars, watch), watch + 30);
                var tracker = pair.Client.Start<Tracked>(s => InGame.Track(s, train.Last, watch), watch + 30);
                var push = pair.Host.Start(() => Cars.Push(train.Loco, train.Dir * o.Speed, o.Hold, 4f), watch + 30);

                // Stall once the train is at speed (half a minute at most).
                float v = await pair.Host.Run<float>(s => Cars.WaitForSpeed(s, train.Loco, 0.8f * o.Speed, 30));
                Require.AtLeast(v, 0.8f * o.Speed, "the train's speed before the stall (m/s)");
                stall = await pair.Host.Run<Stalled>(s => InGame.Stall(s, o.Stall), o.Stall + 60);
                Report.Note($"the host stalled: {stall.Frames} frames, game time {stall.GameSeconds:0.#}s in {stall.RealSeconds:0.#}s");

                await push;
                PeerResults<Watched> watched = await watchers;
                (hostWatch, clientWatch) = (watched.Host, watched.Client);
                track = await tracker;
            }

            // Compare once the train has stopped; the two games are read one after the other.
            Require.AtMost(await pair.Host.Run<float>(s => Cars.WaitUntilStopped(s, train.Loco, 30)), 0.05, "the host's train speed after the push (m/s)");
            await Task.Delay(3000);
            Watched hostFinal = await pair.Host.Run<Watched>(s => InGame.Watch(s, train.Cars, 1));
            Watched clientFinal = await pair.Client.Run<Watched>(s => InGame.Watch(s, train.Cars, 1));

            foreach (var (peer, w) in new[] { ("host", hostWatch), ("client", clientWatch) })
            {
                Expect.Zero(w.WorstUncoupled, $"{peer}: couplings lost");
                Expect.Zero(w.WorstHoses, $"{peer}: hoses off");
                Expect.Zero(w.WorstCocks, $"{peer}: cocks shut");
                Expect.AtLeast(w.MinPipe, 3.5, $"{peer}: lowest brake pipe pressure (bar)");
                Report.Note($"{peer}: loco main reservoir at least {w.MinMainRes:0.##} bar");
            }

            // Snaps are expected with the host in trouble, during the stall and after it: noted, never failed.
            Report.Note($"client last wagon: worst step {track.WorstJump:0.##} m at {track.WorstAt:0.#}s (noted, not failed)");
            foreach (var (when, jumps) in new[]
                     {
                         ("before the stall", track.Jumps.Where(j => j.Utc < stall.StartUtc)),
                         ("during the stall", track.Jumps.Where(j => j.Utc >= stall.StartUtc && j.Utc <= stall.EndUtc)),
                         ("after the stall", track.Jumps.Where(j => j.Utc > stall.EndUtc))
                     })
                if (jumps.Any())
                    Report.Note($"{when}: " + string.Join(" / ", jumps.Take(5).Select(j => $"{j.T:0.#}s jump {j.Size:0.##} m: {j.Note}")));

            Report.Note($"after: host speed {hostFinal.Final.Speed:0.##} m/s, brake pipe host {hostFinal.Final.Pipe:0.##} client {clientFinal.Final.Pipe:0.##} bar");
            Expect.AtMost(Peers.Horizontal(hostFinal.Final.LastAbs, clientFinal.Final.LastAbs), 1, "peers apart on the last wagon (m)");
        }

        public sealed class Watched
        {
            public int WorstUncoupled, WorstHoses, WorstCocks;
            public float MinPipe, MinMainRes;
            public Now Final;
        }

        public sealed class Now
        {
            public float Pipe, Speed;
            public Abs LastAbs;
        }

        public sealed class Tracked
        {
            public Jump[] Jumps;
            public float WorstJump, WorstAt;
        }

        public sealed class Jump
        {
            /// <summary>ms since the Unix epoch: both games run on one machine's clock, so this
            /// compares with the host's stall.</summary>
            public long Utc;
            public float T, Size;
            public string Note;
        }

        public sealed class Stalled
        {
            public int Frames;
            public float GameSeconds, RealSeconds;
            public long StartUtc, EndUtc;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Either peer: watches the train (loco first) and reports what broke: couplings,
            /// hoses, cocks, the lowest brake pipe pressure on any car and the loco's lowest main
            /// reservoir pressure. Each new worst is a note.</summary>
            [Step]
            public static IEnumerator Watch(Step<Watched> step, string[] ids, float seconds)
            {
                var cars = ids.Select(Trains.Find).ToList();
                float minPipe = 99f, minMainRes = 99f;
                int worstUncoupled = 0, worstHoses = 0, worstCocks = 0;
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < seconds;)
                {
                    yield return null;
                    int uncoupled = 0, hoses = 0, cocks = 0;
                    for (int k = 0; k < cars.Count - 1; k++)
                    {
                        var link = new[] { cars[k].frontCoupler, cars[k].rearCoupler }.FirstOrDefault(c => c.coupledTo != null && c.coupledTo.train == cars[k + 1]);
                        if (link == null) { uncoupled++; continue; }
                        if (link.GetAirHoseConnectedTo() != link.coupledTo) hoses++;
                        if (!link.IsCockOpen || !link.coupledTo.IsCockOpen) cocks++;
                    }
                    if ((uncoupled > worstUncoupled || hoses > worstHoses || cocks > worstCocks) && step.Notes.Count < 6)
                        step.Note($"{Time.realtimeSinceStartup - t0:F1}s: {uncoupled} uncoupled, {hoses} hoses off, {cocks} cocks shut");
                    worstUncoupled = Math.Max(worstUncoupled, uncoupled);
                    worstHoses = Math.Max(worstHoses, hoses);
                    worstCocks = Math.Max(worstCocks, cocks);
                    minPipe = Mathf.Min(minPipe, cars.Min(c => c.brakeSystem.brakePipePressure));
                    minMainRes = Mathf.Min(minMainRes, cars[0].brakeSystem.mainReservoirPressure);
                }
                step.Result = new Watched
                {
                    WorstUncoupled = worstUncoupled, WorstHoses = worstHoses, WorstCocks = worstCocks,
                    MinPipe = (float)Math.Round(minPipe, 2), MinMainRes = (float)Math.Round(minMainRes, 2),
                    Final = new Now
                    {
                        Pipe = (float)Math.Round(cars.Min(c => c.brakeSystem.brakePipePressure), 2),
                        LastAbs = Positions.Of(cars.Last().transform.position),
                        Speed = (float)Math.Round(Mathf.Abs(cars[0].GetForwardSpeed()), 2)
                    }
                };
            }

            /// <summary>Client: records the car's absolute position every physics step and reports
            /// every single-step jump over 1 m with when it happened, plus the biggest of them.</summary>
            [Step]
            public static IEnumerator Track(Step<Tracked> step, string id, float seconds)
            {
                TrainCar car = Trains.Find(id);
                var jumps = new List<Jump>();
                float worstJump = 0, worstAt = 0;
                Vector3 lastP = Dev.Abs(car.transform.position);
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < seconds;)
                {
                    yield return new WaitForFixedUpdate();
                    Vector3 now = Dev.Abs(car.transform.position);
                    float jump = Vector3.Distance(now, lastP);
                    float t = Time.realtimeSinceStartup - t0;
                    if (jump > worstJump) { worstJump = jump; worstAt = t; }
                    if (jump > 1f && jumps.Count < 50)
                        jumps.Add(new Jump
                        {
                            Utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            T = (float)Math.Round(t, 1),
                            Size = (float)Math.Round(jump, 2),
                            Note = $"derailed {car.derailed}, kinematic {car.rb.isKinematic}, v {car.GetForwardSpeed():F1}, " +
                                   $"bogies {string.Join("/", car.Bogies.Select(b => (b.track?.name ?? "-") + (b.HasDerailed ? "!" : "")))}, frame {Time.unscaledDeltaTime * 1000f:F0} ms"
                        });
                    lastP = now;
                }
                step.Result = new Tracked { Jumps = jumps.ToArray(), WorstJump = (float)Math.Round(worstJump, 2), WorstAt = (float)Math.Round(worstAt, 1) };
            }

            /// <summary>Host: runs at about 2 fps (each frame sleeps 480 ms), as a host that hitches
            /// badly. The sleep is the condition under test, not a wait.</summary>
            [Step]
            public static IEnumerator Stall(Step<Stalled> step, float seconds)
            {
                long startUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                float t0 = Time.realtimeSinceStartup, g0 = Time.time;
                int frames = 0;
                while (Time.realtimeSinceStartup - t0 < seconds)
                {
                    System.Threading.Thread.Sleep(480);
                    frames++;
                    yield return null;
                }
                step.Result = new Stalled
                {
                    Frames = frames, GameSeconds = Time.time - g0, RealSeconds = Time.realtimeSinceStartup - t0,
                    StartUtc = startUtc, EndUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
        }
    }
}
