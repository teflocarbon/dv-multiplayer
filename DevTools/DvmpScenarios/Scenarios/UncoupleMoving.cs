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
    /// Uncoupling a moving train (upstream #141): the host drives a loco and two wagons and uncouples
    /// the last wagon on the move, then shoves back into it (DV does not couple on contact). The
    /// client must see the wagon roll free without being dragged or snapping, and agree with the host
    /// on where it ends up and whether it is coupled. RIDE=1: the last car is a second loco with the
    /// client riding in its cab (the report's setup).
    /// </summary>
    [Scenario("uncouple-moving", "The host uncouples the last car on the move and shoves back into it: the client sees it roll free and agrees where it ends up and whether it is coupled")]
    [Budget(600)]
    public sealed class UncoupleMoving : Scenario<UncoupleMoving.Options>
    {
        public sealed class Options
        {
            /// <summary>m/s.</summary>
            public float Speed = 5;
            /// <summary>The client's receive lag, ms.</summary>
            public int Lag;
            /// <summary>Runs.</summary>
            public int N = 2;
            /// <summary>The last car's livery.</summary>
            public string Last = "BoxcarBrown";
            /// <summary>The last car is a DE2 with the client in its cab.</summary>
            public bool Ride;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string last = o.Ride ? "LocoDE2" : o.Last;
            // With lag the client sees a change of speed late, and learns of an impact late and has
            // to win the distance back: allow the distance covered meanwhile.
            float slack = 0.5f + 1.5f * (o.Lag / 1000f) * o.Speed;
            using (await pair.Client.Lag(o.Lag))
            {
                for (int run = 1; run <= o.N; run++)
                {
                    Three t = await pair.Host.Run<Three>(s => Cars.SpawnThree(s, last));
                    bool deleted = false;
                    Func<Task> delete = async () =>
                    {
                        if (deleted) return;
                        deleted = true;
                        await pair.Host.Run<string[]>(s => Cars.Delete(s, t.Cars));
                    };
                    pair.Cleanup(delete);
                    await Task.Delay(2000);
                    if (o.Ride) await pair.Client.Run(() => Players.Board(t.Last));

                    var tracks = pair.Both.Start<Tracked>(s => InGame.Track(s, t.Last, 14), 40);
                    await Task.Delay(300);
                    await pair.Host.Run(() => InGame.Drive(t.Loco, t.Last, t.LastFront, o.Speed, 4f), 60);
                    Compare(await tracks, $"run {run} uncouple", slack);

                    tracks = pair.Both.Start<Tracked>(s => InGame.Track(s, t.Last, 25), 60);
                    await Task.Delay(300);
                    bool hostCoupled = await pair.Host.Run<bool>(s => InGame.Reverse(s, t.Loco, t.Last), 60);
                    PeerResults<Tracked> shoved = await tracks;
                    await Task.Delay(2000);
                    bool clientCoupled = await pair.Client.Run(() => Cars.SameSet(t.Loco, t.Last));
                    string label = $"run {run} shoved back into it (coupled: host {hostCoupled}, client {clientCoupled})";
                    Compare(shoved, label, slack);
                    Expect.Equal(hostCoupled, clientCoupled, $"{label}: the client coupled (the host's expected)");
                    await delete();
                }
            }
        }

        private static void Compare(PeerResults<Tracked> r, string label, float slack)
        {
            Tracked h = r.Host, c = r.Client;
            float apart = Peers.Horizontal(h.Final, c.Final);
            Report.Note($"{label}: wagon moved {h.Travelled} m (client {c.Travelled}), apart {apart:0.00} m, client worst step {c.WorstJump} m at {c.WorstAt} s");
            bool ok = Expect.AtMost(apart, slack, $"{label}: the peers apart on the wagon (m)");
            ok &= Expect.AtMost(c.WorstJump, 1.0, $"{label}: the client's worst single-step jump (m)");
            // Buffers can bounce a shoved wagon back on both peers; only extra travel on the client is a snap.
            ok &= Expect.AtMost(c.Overshoot - h.Overshoot, slack, $"{label}: the client wagon dragged or snapped back beyond the host's (m; client {c.Overshoot}, host {h.Overshoot})");
            if (!ok && c.Notes.Length > 0) Report.Note("client: " + string.Join(" / ", c.Notes));
        }

        public sealed class Tracked
        {
            public string[] Notes;
            public float WorstJump, WorstAt, Travelled, Overshoot;
            public Abs Final;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Either peer: records the car's absolute position every physics step; reports
            /// the biggest single-step jump, how far it travelled, how far beyond where it ends it got
            /// (a wagon dragged along and snapped back shows here), and its final position.</summary>
            [Step]
            public static IEnumerator Track(Step<Tracked> step, string id, float seconds)
            {
                var car = Trains.Find(id);
                var pts = new List<(float t, Vector3 p)>();
                var notes = new List<string>();
                Vector3 lastP = Dev.Abs(car.transform.position);
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < seconds;)
                {
                    yield return new WaitForFixedUpdate();
                    Vector3 now = Dev.Abs(car.transform.position);
                    if (Vector3.Distance(now, lastP) > 1f && notes.Count < 5)
                        notes.Add($"{Time.realtimeSinceStartup - t0:F1}s jump {Vector3.Distance(now, lastP):F1} m: derailed {car.derailed}, kinematic {car.rb.isKinematic}, v {car.GetForwardSpeed():F1}, " +
                                  $"bogies {string.Join("/", car.Bogies.Select(b => (b.track?.name ?? "-") + (b.HasDerailed ? "!" : "")))}, frame {Time.unscaledDeltaTime * 1000f:F0} ms");
                    lastP = now;
                    pts.Add((Time.realtimeSinceStartup - t0, now));
                }
                float worstJump = 0, worstAt = 0;
                for (int i = 1; i < pts.Count; i++)
                {
                    float jump = Vector3.Distance(pts[i].p, pts[i - 1].p);
                    if (jump > worstJump) { worstJump = jump; worstAt = pts[i].t; }
                }
                Vector3 end = pts.Last().p;
                step.Result = new Tracked
                {
                    Notes = notes.ToArray(),
                    WorstJump = (float)Math.Round(worstJump, 2), WorstAt = (float)Math.Round(worstAt, 1),
                    Travelled = (float)Math.Round(Vector3.Distance(pts.First().p, end), 1),
                    Overshoot = (float)Math.Round(pts.Max(p => Vector3.Distance(p.p, end)) - Vector3.Distance(pts.First().p, end), 2),
                    Final = new Abs { X = end.x, Y = end.y, Z = end.z }
                };
            }

            /// <summary>Host: drives the loco's trainset away from the wagons at <paramref name="speed"/>
            /// m/s, uncouples the last car <paramref name="at"/> seconds in, keeps driving the front
            /// part for 6 s more, then stops it.</summary>
            [Step]
            public static IEnumerator Drive(string locoId, string lastId, bool lastFront, float speed, float at)
            {
                var loco = Trains.Find(locoId); var last = Trains.Find(lastId);
                Coupler link = lastFront ? last.frontCoupler : last.rearCoupler;
                foreach (var c in loco.trainset.cars) c.brakeSystem.SetHandbrakePosition(0f);
                Vector3 fwd = loco.transform.forward;
                if (Vector3.Dot(fwd, last.transform.position - loco.transform.position) > 0) fwd = -fwd;
                bool uncoupled = false;
                float t0 = Time.time;
                while (Time.time - t0 < at + 6f)
                {
                    yield return new WaitForFixedUpdate();
                    float t = Time.time - t0;
                    if (!uncoupled && t >= at) { link.Uncouple(true, false, false, false); uncoupled = true; }
                    float v = speed * Mathf.Clamp01(t / 2f);
                    foreach (var c in loco.trainset.cars)
                    {
                        Vector3 dir = c.transform.forward * Mathf.Sign(Vector3.Dot(c.transform.forward, fwd));
                        c.rb.AddForce(dir * v - Vector3.Project(c.rb.velocity, dir), ForceMode.VelocityChange);
                    }
                }
                foreach (var c in loco.trainset.cars) { c.rb.velocity = Vector3.zero; c.brakeSystem.SetHandbrakePosition(1f); }
            }

            /// <summary>Host: reverses the loco's part back into the stopped wagon for 12 s (a
            /// shunting shove; DV does not couple on contact). Result: whether they're one trainset.</summary>
            [Step]
            public static IEnumerator Reverse(Step<bool> step, string locoId, string lastId)
            {
                var loco = Trains.Find(locoId); var last = Trains.Find(lastId);
                foreach (var c in loco.trainset.cars) c.brakeSystem.SetHandbrakePosition(0f);
                last.brakeSystem.SetHandbrakePosition(1f);
                for (float t0 = Time.time; Time.time - t0 < 12f;)
                {
                    yield return new WaitForFixedUpdate();
                    Vector3 toward = (last.transform.position - loco.transform.position).normalized;
                    foreach (var c in loco.trainset.cars)
                    {
                        Vector3 dir = c.transform.forward * Mathf.Sign(Vector3.Dot(c.transform.forward, toward));
                        c.rb.AddForce(dir * 1.5f - Vector3.Project(c.rb.velocity, dir), ForceMode.VelocityChange);
                    }
                }
                for (int i = 0; i < 50; i++) { yield return new WaitForFixedUpdate(); foreach (var c in loco.trainset.cars) c.rb.velocity = Vector3.zero; }
                foreach (var c in loco.trainset.cars) c.brakeSystem.SetHandbrakePosition(1f);
                step.Result = loco.trainset == last.trainset;
            }
        }
    }
}
