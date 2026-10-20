using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Coupling on impact (the "bounce" report): the host runs a loco into a standing wagon a bit too
    /// fast and a player couples the moment the couplers meet, while the buffers are still bouncing.
    /// Both peers must agree whether the cars are coupled, and when the host pulls away the wagon must
    /// follow (or stay) on both. When they disagree, how the client is holding each car (#175) is
    /// noted, then and 12 s later.
    /// </summary>
    [Scenario("couple-impact", "A player couples as the buffers bounce after a hard hit: both peers agree whether the cars are coupled, and the wagon follows (or stays) when the host pulls away")]
    [Budget(900)]
    public sealed class CoupleImpact : Scenario<CoupleImpact.Options>
    {
        public sealed class Options
        {
            /// <summary>m/s, a run each.</summary>
            public float[] Speeds = { 2, 3, 4 };
            /// <summary>Who couples: host or client.</summary>
            public string Who = "client";
            /// <summary>Hook the chain instead of a full couple.</summary>
            public bool Chain;
            /// <summary>The wagon's handbrake at the hit.</summary>
            public bool WagonBrake = true;
            /// <summary>The client's receive lag, ms.</summary>
            public int Lag;
            /// <summary>ms: the coupling peer stalls four frames that long just after coupling.</summary>
            public int Hitch;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Peer who = o.Who == "host" ? pair.Host : o.Who == "client" ? pair.Client : throw new ArgumentException("WHO is host or client");
            using (await pair.Client.Lag(o.Lag))
            {
                foreach (float speed in o.Speeds)
                {
                    Coupling c = await pair.Host.Run<Coupling>(s => Couplings.SpawnApart(s, "BoxcarBrown", 12f), 90);
                    bool deleted = false;
                    Func<Task> delete = async () =>
                    {
                        if (deleted) return;
                        deleted = true;
                        await pair.Host.Run<string[]>(s => Cars.Delete(s, c.Cars));
                    };
                    pair.Cleanup(delete);
                    await Task.Delay(2000);

                    var coupling = who.Start<Coupled>(s => InGame.Couple(s, c, o.Chain, o.Hitch), 60);
                    Rammed r = await pair.Host.Run<Rammed>(s => InGame.Ram(s, c, speed, o.WagonBrake), 60);
                    Coupled k = await coupling;
                    Report.Note($"{speed} m/s: hit at {r.HitSpeed} m/s, buffers closed to {r.MinGap} m; {o.Who} coupled at gap {k.GapAt} via {k.How}, chain {k.Fsm} (now {k.CoupledNow})");

                    // Let the bounce and any roll die out (the peers are read one after the other).
                    for (int i = 0; i < 12; i++)
                    {
                        await Task.Delay(2000);
                        if ((await pair.Host.Run(() => Couplings.State(c))).Speed == 0) break;
                    }
                    await Task.Delay(2000);
                    if (!await Agree(pair, c, $"{speed} m/s, after impact")) await Hold(pair, c, $"{speed} m/s");
                    await pair.Host.Run<bool>(s => Couplings.PullAway(s, c), 60);
                    await Task.Delay(3000);
                    if (!await Agree(pair, c, $"{speed} m/s, after pulling away")) await Hold(pair, c, $"{speed} m/s");
                    await delete();
                }
            }
        }

        /// <summary>Both peers agree whether the cars are coupled, and where the wagon is.</summary>
        private static async Task<bool> Agree(Pair pair, Coupling c, string label)
        {
            CouplingState h = await pair.Host.Run(() => Couplings.State(c));
            CouplingState k = await pair.Client.Run(() => Couplings.State(c));
            float apart = Peers.Horizontal(h.WagonAbs, k.WagonAbs);
            Report.Note($"{label}: coupled {h.Coupled}, gap host {h.Gap} client {k.Gap}, chain host {h.Chain} client {k.Chain}");
            bool ok = Expect.That(h.Coupled == k.Coupled && h.SameSet == k.SameSet, $"{label}: the peers agree whether the cars are coupled",
                $"coupled/one set: host {h.Coupled}/{h.SameSet}, client {k.Coupled}/{k.SameSet}");
            return Expect.AtMost(apart, 1, $"{label}: wagon apart (m)") && ok;
        }

        /// <summary>Off the host's: how the client is holding each car (#175), now and 12 s later (past
        /// a give-up's retry), and whether the peers agree then. Notes only.</summary>
        private static async Task Hold(Pair pair, Coupling c, string label)
        {
            foreach (int wait in new[] { 0, 12 })
            {
                await Task.Delay(wait * 1000);
                Report.Note($"client hold, +{wait} s: " + await pair.Client.Run(() => InGame.Hold(c)));
            }
            CouplingState h = await pair.Host.Run(() => Couplings.State(c));
            CouplingState k = await pair.Client.Run(() => Couplings.State(c));
            Report.Note($"{label}, 12 s later (noted, not failed): host {h}; client {k}; wagon apart {Peers.Horizontal(h.WagonAbs, k.WagonAbs):0.##} m");
        }

        public sealed class Coupled
        {
            public string How, Fsm;
            public float GapAt;
            public bool CoupledNow;
        }

        public sealed class Rammed
        {
            public float HitSpeed, MinGap;
            public bool Coupled;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Either peer: waits for the couplers to come into range and couples then, as a player
            /// using the couple key or coupling menu at the moment of contact, or (<paramref name="viaChain"/>)
            /// grabbing the wagon's chain and dropping it on the loco's hook. <paramref name="hitch"/> ms:
            /// stalls four frames that long during the bounce, as a stutter would.
            /// </summary>
            [Step]
            public static IEnumerator Couple(Step<Coupled> step, Coupling coupling, bool viaChain, int hitch)
            {
                var (ca, cb) = Couplings.Ends(coupling);
                // Stand beside the wagon's coupler, as a player waiting to couple would (the host checks it).
                if (!NetworkLifecycle.Instance.IsHost())
                    yield return LocalPlayer.TeleportToGround(Dev.Abs(cb.transform.position).x + 2.5f, Dev.Abs(cb.transform.position).z);
                var optimizers = viaChain ? Couplings.LockChains(ca.train, cb.train) : new System.Collections.Generic.List<ChainCouplerVisibilityOptimizer>();
                var helper = new DV.HUD.UICouplingHelper { trainCar = cb.train, shouldAutoHandbrake = () => false };
                float gapAt = -1;
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 30f;)
                {
                    yield return new WaitForFixedUpdate();
                    float gap = Vector3.Distance(ca.transform.position, cb.transform.position);
                    if (gap >= (viaChain ? 1.6f : 0.9f)) continue;
                    gapAt = gap;
                    if (!viaChain) { helper.HandleCoupling(cb, false); break; }
                    // Grab the chain as the cars close and hold it on the hook through the bounce.
                    var chain = cb.ChainScript; var other = ca.ChainScript;
                    chain.OnGrabbed(null);
                    for (int i = 0; i < 40; i++) { chain.knob.transform.position = other.ownAttachPoint.transform.position; yield return null; }
                    chain.OnUngrabbed(null);
                    break;
                }
                for (int i = 0; i < 4 && hitch > 0; i++) { System.Threading.Thread.Sleep(hitch); yield return null; }   // the stutter under test
                for (int i = 0; i < 30; i++) yield return new WaitForFixedUpdate();
                foreach (var o in optimizers) o.UnlockLOD();
                step.Result = new Coupled { How = viaChain ? "chain" : "menu", GapAt = (float)Math.Round(gapAt, 2), CoupledNow = ca.coupledTo == cb, Fsm = cb.ChainScript?.CurrentState.ToString() ?? "-" };
            }

            /// <summary>Host: runs the loco into the wagon at <paramref name="speed"/> m/s and coasts
            /// from 3 m out (a driver who left it a bit late). The wagon's handbrake is set to
            /// <paramref name="wagonBrake"/> from 5 m out: a free wagon on a grade would roll off before
            /// the loco got there.</summary>
            [Step]
            public static IEnumerator Ram(Step<Rammed> step, Coupling coupling, float speed, bool wagonBrake)
            {
                var (ca, cb) = Couplings.Ends(coupling);
                TrainCar loco = ca.train, wagon = cb.train;
                loco.brakeSystem.SetHandbrakePosition(0f);
                float minGap = 99f, hitSpeed = 0f;
                for (float t0 = Time.time; Time.time - t0 < 25f;)
                {
                    yield return new WaitForFixedUpdate();
                    float gap = Vector3.Distance(ca.transform.position, cb.transform.position);
                    minGap = Mathf.Min(minGap, gap);
                    if (gap < 5f) wagon.brakeSystem.SetHandbrakePosition(wagonBrake ? 1f : 0f);
                    if (gap < 3f) { hitSpeed = Mathf.Max(hitSpeed, Mathf.Abs(loco.GetForwardSpeed())); break; }
                    Vector3 toward = (wagon.transform.position - loco.transform.position).normalized;
                    Vector3 dir = loco.transform.forward * Mathf.Sign(Vector3.Dot(loco.transform.forward, toward));
                    loco.rb.AddForce(dir * speed - Vector3.Project(loco.rb.velocity, dir), ForceMode.VelocityChange);
                }
                for (float t0 = Time.time; Time.time - t0 < 8f;) { yield return new WaitForFixedUpdate(); minGap = Mathf.Min(minGap, Vector3.Distance(ca.transform.position, cb.transform.position)); }
                loco.brakeSystem.SetHandbrakePosition(1f);
                step.Result = new Rammed { HitSpeed = (float)Math.Round(hitSpeed, 2), MinGap = (float)Math.Round(minGap, 2), Coupled = ca.coupledTo == cb };
            }

            /// <summary>
            /// Client: how its TrainSpeedQueue is holding the loco and the wagon, for when they're
            /// found off the host's (#175: after a 3 m/s impact both stood coupled 1.1 m from the
            /// host's until it pulled away). A stopped coupled car is driven back at 0.2-0.5 m/s
            /// (HoldCoupled); one that won't move in 10 s is left for 10 s before another try. Per
            /// car: the queue's state, its along-track error to the host's bogies, and what might stop
            /// it moving (kinematic, asleep, braked).
            /// </summary>
            [Step]
            public static string Hold(Coupling coupling)
            {
                string Of(string id)
                {
                    var car = Trains.Find(id);
                    var q = car.GetComponent<Multiplayer.Client.Trains.TrainSpeedQueue>();
                    if (q == null) return id + ": no queue";
                    bool hasError = q.TryGetAlongTrackError(out float error);
                    string Since(float t) => float.IsNegativeInfinity(t) ? "-1" : Math.Round(Time.time - t, 1).ToString();
                    return $"{id}: held {q.Held}, following {q.Following}, placing {q.Placing}, parked {q.Parked}, host coupled {q.HostCoupled}, driving {q.driving}, " +
                           $"error {(hasError ? Math.Round(error, 3).ToString() : "none")}, host speed {Math.Round(q.targetSpeed, 3)}, sample age {Since(q.lastSampleTime)}, " +
                           $"driving for {Since(q.holdDriveSince)}, retry in {(float.IsNegativeInfinity(q.holdRetryAt) ? -1f : Math.Round(q.holdRetryAt - Time.time, 1))}, off for {Since(q.holdOffSince)}, " +
                           $"kinematic {car.rb.isKinematic}, asleep {car.rb.IsSleeping()}, speed {Math.Round(car.rb.velocity.magnitude, 3)}, " +
                           $"handbrake {(car.brakeSystem.hasHandbrake ? Math.Round(car.brakeSystem.handbrakePosition, 2) : -1)}, braking {Math.Round(car.brakeSystem.brakingFactor, 2)}, set of {car.trainset?.cars.Count ?? 0}";
                }
                return $"{Of(coupling.Loco)} | {Of(coupling.Wagon)} (at {Math.Round(Time.time, 1)})";
            }
        }
    }
}
