using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A derailed car dragged at speed between on-rail cars (playtest 2026-10-19: "carriages
    /// juddering like crazy"). The host spawns a loco and two wagons, derails the middle one and drags
    /// the train by the loco. On the client the derailed car must follow as smoothly as on the host:
    /// the gap between it and the loco may change no faster than RATIO times the host's (RMS, with a
    /// floor of FLOOR m/s), and no coupling may break on either game. Before the fix the client aimed
    /// the car ~0.3 m behind its neighbours and overwrote its velocity on every sample, jolting it
    /// against its couplers 24 times a second.
    /// </summary>
    [Scenario("derail-drag", "A derailed car dragged between on-rail cars follows as smoothly on the client as on the host, and no coupling breaks")]
    [Budget(300)]
    public sealed class DerailDrag : Scenario<DerailDrag.Options>
    {
        public sealed class Options
        {
            /// <summary>m/s (about 22 km/h).</summary>
            public float Speed = 6;
            public int DragSeconds = 15;
            /// <summary>The client's gap change may be this many times the host's.</summary>
            public float Ratio = 3;
            /// <summary>m/s: the client's limit is at least this.</summary>
            public float Floor = 0.3f;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Three t = await pair.Host.Run<Three>(s => Cars.SpawnThree(s, "FlatbedEmpty"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, t.Cars)));
            await Task.Delay(3000);
            Report.Note($"the host derails {t.Mid} (between {t.Loco} and {t.Last})");
            Require.That(await pair.Host.Run(() => Cars.Derail(t.Mid)), "the test derail happened on the host");
            await Task.Delay(3000);
            Require.That(await pair.Client.Run(() => Cars.Derailed(t.Mid)), "the derail reached the client");

            Report.Note($"the host drags the train at {o.Speed} m/s for {o.DragSeconds} s; both games watch the derailed car's coupling");
            var drag = pair.Host.Start(() => InGame.Drag(t.Loco, new[] { t.Mid, t.Last }, o.Speed, o.DragSeconds), o.DragSeconds + 30);
            await Task.Delay(1000);
            PeerResults<Gap> gaps = await pair.Both.Run<Gap>(s => InGame.Watch(s, t.Loco, t.Mid, o.DragSeconds - 2, 4f), o.DragSeconds + 30);
            await drag;
            foreach (var (peer, r) in gaps)
                Report.Note($"{peer}: gap change RMS {r.Rms} m/s, worst {r.Worst} m/s, gap range {r.GapRange} m, velocity change RMS loco {r.LocoAccel} / car {r.CarAccel} m/s2, " +
                            $"{r.SpeedKmh} km/h, derailed {r.Derailed}, coupled {r.Coupled}, {r.Samples} samples");
            Expect.That(gaps.Host.Coupled, "the derailed car stayed on the train on the host");
            Expect.That(gaps.Client.Coupled, "the derailed car stayed on the train on the client");
            Expect.AtMost(gaps.Client.Rms, Math.Max(o.Floor, o.Ratio * gaps.Host.Rms),
                $"the client's gap change at the derailed car's coupling (RMS m/s; the host's {gaps.Host.Rms} x {o.Ratio}, at least {o.Floor})");
        }

        public sealed class Gap
        {
            public int Samples;
            public float Rms, Worst, GapRange, LocoAccel, CarAccel, SpeedKmh;
            public bool Derailed, Coupled;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: drags the train by its loco alone (the derailed car is pulled by its
            /// couplers, the last car rolls on the rails behind it) at <paramref name="speed"/> m/s for
            /// <paramref name="seconds"/>, then stops it.</summary>
            [Step]
            public static IEnumerator Drag(string locoId, string[] others, float speed, float seconds)
            {
                var loco = Trains.Find(locoId);
                var behind = others.Select(Trains.Find).ToList();
                foreach (var c in loco.trainset.cars) c.brakeSystem.SetHandbrakePosition(0f);
                Vector3 fwd = loco.transform.forward;
                if (Vector3.Dot(fwd, behind[0].transform.position - loco.transform.position) > 0) fwd = -fwd;
                for (float t0 = Time.time; Time.time - t0 < seconds;)
                {
                    yield return new WaitForFixedUpdate();
                    float v = speed * Mathf.Clamp01((Time.time - t0) / 3f);
                    Vector3 dir = loco.transform.forward * Mathf.Sign(Vector3.Dot(loco.transform.forward, fwd));
                    loco.rb.AddForce(dir * v - Vector3.Project(loco.rb.velocity, dir), ForceMode.VelocityChange);
                }
                foreach (var c in loco.trainset.cars.Concat(behind).Distinct()) { c.rb.velocity = Vector3.zero; c.brakeSystem.SetHandbrakePosition(1f); }
            }

            /// <summary>
            /// Either peer: while the train moves, samples every physics step the gap between the
            /// loco's coupler and the derailed car's (the pair nearest each other). Dragged smoothly,
            /// the gap barely changes; a judder (the derailed car yanked back and forth against its
            /// couplers) makes it swing every step. Reports how fast the gap changes (RMS and the worst
            /// step, in m/s), its range, the end state, and how much the loco's and the derailed car's
            /// own velocities change each step (RMS, m/s^2): which of the two shakes, when the gap
            /// does. The first <paramref name="settle"/> seconds are skipped while the train gets going.
            /// </summary>
            [Step]
            public static IEnumerator Watch(Step<Gap> step, string locoId, string carId, float seconds, float settle)
            {
                var loco = Trains.Find(locoId); var car = Trains.Find(carId);
                var (a, b) = Couplings.Facing(loco, car);
                float t0 = Time.time, prev = float.NaN, sumSq = 0f, worst = 0f, min = float.MaxValue, max = float.MinValue, speedSum = 0f;
                int n = 0;
                Vector3 lastPos = loco.transform.position;
                Vector3 locoV = loco.rb.velocity, carV = car.rb.velocity;
                float locoSq = 0f, carSq = 0f;
                while (Time.time - t0 < seconds)
                {
                    yield return new WaitForFixedUpdate();
                    float gap = Vector3.Distance(a.transform.position, b.transform.position);
                    float moved = Vector3.Distance(loco.transform.position, lastPos);
                    lastPos = loco.transform.position;
                    if (Time.time - t0 < settle || moved > 5f) { prev = gap; continue; }   // > 5 m in a step: a world shift
                    if (!float.IsNaN(prev))
                    {
                        float rate = Mathf.Abs(gap - prev) / Time.fixedDeltaTime;
                        sumSq += rate * rate; worst = Mathf.Max(worst, rate); n++;
                        speedSum += moved / Time.fixedDeltaTime;
                        locoSq += ((loco.rb.velocity - locoV) / Time.fixedDeltaTime).sqrMagnitude;
                        carSq += ((car.rb.velocity - carV) / Time.fixedDeltaTime).sqrMagnitude;
                    }
                    locoV = loco.rb.velocity; carV = car.rb.velocity;
                    min = Mathf.Min(min, gap); max = Mathf.Max(max, gap);
                    prev = gap;
                }
                step.Result = new Gap
                {
                    Samples = n,
                    Rms = n > 0 ? (float)Math.Round(Mathf.Sqrt(sumSq / n), 3) : -1,
                    Worst = (float)Math.Round(worst, 2),
                    GapRange = n > 0 ? (float)Math.Round(max - min, 3) : -1,
                    LocoAccel = n > 0 ? (float)Math.Round(Mathf.Sqrt(locoSq / n), 2) : -1,
                    CarAccel = n > 0 ? (float)Math.Round(Mathf.Sqrt(carSq / n), 2) : -1,
                    SpeedKmh = n > 0 ? (float)Math.Round(speedSum / n * 3.6f, 1) : 0,
                    Derailed = car.derailed,
                    Coupled = car.trainset == loco.trainset
                };
            }
        }
    }
}
