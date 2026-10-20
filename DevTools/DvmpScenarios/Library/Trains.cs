// Train steps more than one scenario uses: spawning the small consists the coupling and derail
// scenarios start from, a coupling's state, working a coupling by hand, derailing. Each [Step]
// runs inside a game: call it through pair.Host / pair.Client / pair.Both, never directly.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Coverage;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>A loco and a wagon and the two couplers that face each other (Couplings.Spawn*).</summary>
    public sealed class Coupling
    {
        public string Loco, Wagon;
        public bool LocoFront, WagonFront;
        /// <summary>The couplers' distance apart when spawned (m).</summary>
        public float Gap;
        /// <summary>Where the loco's coupler is.</summary>
        public Abs At;
        public string[] Cars => new[] { Loco, Wagon };
    }

    /// <summary>A coupling as one game has it (Couplings.State).</summary>
    public sealed class CouplingState
    {
        public bool Coupled, SameSet, Hose;
        /// <summary>The loco's and the wagon's angle cocks: O open, C closed.</summary>
        public string Cocks;
        /// <summary>Each coupler's chain state, loco's/wagon's.</summary>
        public string Chain;
        /// <summary>Each chain script's state, loco's/wagon's.</summary>
        public string Fsm;
        public float Gap, Speed;
        public Abs WagonAbs;

        public override string ToString() => $"coupled {Coupled}, one set {SameSet}, hose {Hose}, cocks {Cocks}, chain {Chain}, gap {Gap} m";
    }

    /// <summary>A loco and two wagons, coupled (Cars.SpawnThree).</summary>
    public sealed class Three
    {
        public string Loco, Mid, Last;
        /// <summary>Whether the last car's front coupler is the one on the middle car.</summary>
        public bool LastFront;
        public string[] Cars => new[] { Loco, Mid, Last };
    }

    /// <summary>A car spawned next to the client's player, far from the host's body (Cars.SpawnByClient).</summary>
    public sealed class FarCar
    {
        public string Id, Player;
        public bool Ground, Coverage, Dedicated;
        public float Waited;
    }

    /// <summary>How a car lies (Cars.Rest).</summary>
    public sealed class CarRest
    {
        public bool Derailed, Kinematic, Loaded, Ground;
        public string GroundCollider;
        public float Speed, FromHostBody;
        public Abs Abs;
    }

    public static partial class Cars
    {
        /// <summary>Cars a scenario's own spawn step made, for the leak check (they must be gone after the run).</summary>
        internal static void Record(IEnumerable<TrainCar> cars)
        {
            foreach (TrainCar c in cars) if (c != null) spawned.Add(c.ID);
        }

        /// <summary>
        /// Host: a DE2 and two wagons (the last <paramref name="last"/>) on a track near the client's
        /// player, coupled (chain tight, hoses joined, cocks open), handbrakes on.
        /// </summary>
        [Step]
        public static IEnumerator SpawnThree(Step<Three> step, string last)
        {
            var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First();
            foreach (float d in new[] { 20f, 40f, 60f, 80f })
            foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
            {
                var t = CarSpawner.GetTrackClosestTo(client.transform.position + dir * d, 60f, out int _);
                if (t == null) continue;
                var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<DV.ThingTypes.TrainCarLivery> { Livery("LocoDE2"), Livery("BoxcarBrown"), Livery(last) },
                    new List<bool> { false, false, false }, t, true, false);
                if (cars == null) continue;
                Record(cars);
                for (int i = 0; i < 60; i++) yield return null;
                for (int k = 0; k < 2; k++)
                {
                    var (ca, cb) = Couplings.Facing(cars[k], cars[k + 1]);
                    if (ca.coupledTo == null) ca.CoupleTo(cb, false, false);
                    ca.ConnectAirHose(cb, false); ca.IsCockOpen = true; cb.IsCockOpen = true;
                }
                foreach (var c in cars) c.brakeSystem.SetHandbrakePosition(1f);
                for (int i = 0; i < 60; i++) yield return null;
                var link = new[] { cars[2].frontCoupler, cars[2].rearCoupler }.First(c => c.coupledTo != null && c.coupledTo.train == cars[1]);
                step.Result = new Three { Loco = cars[0].ID, Mid = cars[1].ID, Last = cars[2].ID, LastFront = link.isFrontCoupler };
                yield break;
            }
            throw new Exception("no room to spawn the train");
        }

        /// <summary>Derails the car in this game. Result: whether it's derailed now.</summary>
        [Step]
        public static bool Derail(string car)
        {
            TrainCar c = Trains.Find(car);
            c.Derail();
            return c.derailed;
        }

        [Step]
        public static bool Derailed(string car) => Trains.Find(car).derailed;

        /// <summary>Whether the two cars are in one trainset in this game.</summary>
        [Step]
        public static bool SameSet(string a, string b) => Trains.Find(a).trainset == Trains.Find(b).trainset;

        /// <summary>
        /// Host: a car of <paramref name="livery"/> (handbrake on) on the track nearest the client's
        /// player, not the host's own, then up to 120 s for the host to have ground under it (the
        /// server loads every Near tile in the background after a load).
        /// </summary>
        [Step]
        public static IEnumerator SpawnByClient(Step<FarCar> step, string livery)
        {
            var lifecycle = NetworkLifecycle.Instance;
            var player = Multiplayer.Server.Session.NetworkServer.Instance.GameplayPlayers.First(p => p != Multiplayer.Server.Session.NetworkServer.Instance.LocalPlayer);
            var car = CarSpawner.Instance.SpawnCarOnClosestTrack(player.WorldPosition, Livery(livery), false, true, false)
                      ?? throw new Exception("no room to spawn next to the client's player");
            Record(new[] { car });
            car.brakeSystem.SetHandbrakePosition(1f);
            for (int i = 0; i < 180; i++) yield return null;
            float start = Time.realtimeSinceStartup;
            bool ground = false;
            while (Time.realtimeSinceStartup - start < 120f)
            {
                ground = ServerWorldCoverage.Instance != null ? ServerWorldCoverage.MayMove(car) :
                    UnityEngine.Object.FindObjectOfType<WorldStreamingInit>().IsSceneAndTerrainCellLoaded(car.transform.position);
                if (ground) break;
                yield return new WaitForSecondsRealtime(1f);
            }
            step.Result = new FarCar
            {
                Id = car.ID, Player = player.Username, Ground = ground, Waited = Mathf.Round(Time.realtimeSinceStartup - start),
                Coverage = ServerWorldCoverage.Instance != null, Dedicated = lifecycle.IsDedicatedHost
            };
        }

        /// <summary>
        /// The car's physical state in this game: derailed, kinematic (DV freezes a derailed car
        /// whose world isn't loaded), whether its world cell is loaded, whether there's ground under
        /// it, its speed, where it is, and how far it is from this game's own player (or a dedicated
        /// host's parked body).
        /// </summary>
        [Step]
        public static CarRest Rest(string car)
        {
            var c = Trains.Find(car);
            var streaming = UnityEngine.Object.FindObjectOfType<WorldStreamingInit>();
            var below = Physics.RaycastAll(c.transform.position + Vector3.up * 3f, Vector3.down, 12f, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => h.collider.attachedRigidbody != c.rb && !h.transform.IsChildOf(c.transform))
                .OrderBy(h => h.distance).ToList();
            Vector3 abs = Dev.Abs(c.transform.position);
            return new CarRest
            {
                Derailed = c.derailed,
                Kinematic = c.rb.isKinematic,
                Loaded = streaming != null && streaming.IsSceneAndTerrainCellLoaded(c.transform.position),
                Ground = below.Count > 0,
                GroundCollider = below.Count > 0 ? below[0].collider.GetType().Name + " " + below[0].collider.name : null,
                Speed = (float)Math.Round(c.rb.velocity.magnitude, 2),
                Abs = new Abs { X = abs.x, Y = abs.y, Z = abs.z },
                FromHostBody = PlayerManager.PlayerTransform == null ? -1 : Mathf.Round(Vector3.Distance(c.transform.position, PlayerManager.PlayerTransform.position))
            };
        }

        internal static DV.ThingTypes.TrainCarLivery Livery(string id) =>
            DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id == id) ?? throw new Exception("no livery " + id);

        /// <summary>A track's length along its points (m), or 0 for a track without them. Its curve
        /// (BezierCurve) isn't used: that type's assembly isn't among the game DLLs CI builds against.</summary>
        internal static float Length(RailTrack t)
        {
            try { return (float)t.GetKinkedPointSet().span; }
            catch (Exception) { return 0f; }
        }

        /// <summary>The world position halfway along a track's points.</summary>
        internal static Vector3 Middle(RailTrack t)
        {
            var points = t.GetKinkedPointSet().points;
            double half = points[points.Length - 1].span / 2;
            return (Vector3)points.OrderBy(p => Math.Abs(p.span - half)).First().position + WorldMover.currentMove;
        }
    }

    public static class Couplings
    {
        /// <summary>
        /// Host: a DE2 and a <paramref name="wagon"/> on a track near the client's player
        /// (<paramref name="near"/> "client") or the host's (<paramref name="near"/> "host"), coupled
        /// the way a player leaves them (chain tight, hoses connected, cocks open), both braked.
        /// </summary>
        [Step]
        public static IEnumerator SpawnCoupled(Step<Coupling> step, string wagon, string near)
        {
            Vector3 from = near == "host" ? PlayerManager.PlayerTransform.position
                : UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First().transform.position;
            foreach (float d in new[] { 15f, 30f, 45f, 60f, 80f })
            foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
            {
                var t = CarSpawner.GetTrackClosestTo(from + dir * d, 40f, out int _);
                if (t == null) continue;
                var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<DV.ThingTypes.TrainCarLivery> { Cars.Livery("LocoDE2"), Cars.Livery(wagon) }, new List<bool> { false, false }, t, true, false);
                if (cars == null) continue;
                Cars.Record(cars);
                foreach (var c in cars) c.brakeSystem.SetHandbrakePosition(1f); // no air yet: without handbrakes the pair can roll away
                for (int i = 0; i < 60; i++) yield return null;
                var (ca, cb) = Facing(cars[0], cars[1]);
                if (ca.coupledTo == null) ca.CoupleTo(cb, false, false);
                ca.ConnectAirHose(cb, false); ca.IsCockOpen = true; cb.IsCockOpen = true;
                for (int i = 0; i < 60; i++) yield return null;
                step.Result = Of(cars[0], cars[1], ca, cb);
                yield break;
            }
            throw new Exception("no room to spawn the pair");
        }

        /// <summary>
        /// Host: a DE2 and a <paramref name="wagon"/> near the client's player, uncoupled, the loco
        /// backed off until the couplers are <paramref name="gap"/> metres apart. Handbrakes on.
        /// </summary>
        [Step]
        public static IEnumerator SpawnApart(Step<Coupling> step, string wagon, float gap)
        {
            var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First();
            foreach (float d in new[] { 20f, 40f, 60f, 80f })
            foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
            {
                var t = CarSpawner.GetTrackClosestTo(client.transform.position + dir * d, 60f, out int _);
                if (t == null) continue;
                var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<DV.ThingTypes.TrainCarLivery> { Cars.Livery("LocoDE2"), Cars.Livery(wagon) }, new List<bool> { false, false }, t, true, false);
                if (cars == null) continue;
                Cars.Record(cars);
                for (int i = 0; i < 60; i++) yield return null;
                var loco = cars[0]; var w = cars[1];
                var (ca, cb) = Facing(loco, w);
                if (ca.coupledTo != null) ca.Uncouple(false, false, false, false);
                w.brakeSystem.SetHandbrakePosition(1f);
                loco.brakeSystem.SetHandbrakePosition(0f);
                // Back the loco off.
                for (float t0 = Time.time; Time.time - t0 < 20f && Vector3.Distance(ca.transform.position, cb.transform.position) < gap;)
                {
                    yield return new WaitForFixedUpdate();
                    Vector3 away = (loco.transform.position - w.transform.position).normalized;
                    Vector3 dirL = loco.transform.forward * Mathf.Sign(Vector3.Dot(loco.transform.forward, away));
                    loco.rb.AddForce(dirL * 3f - Vector3.Project(loco.rb.velocity, dirL), ForceMode.VelocityChange);
                }
                for (int i = 0; i < 100; i++) { yield return new WaitForFixedUpdate(); loco.rb.velocity = Vector3.zero; }
                loco.brakeSystem.SetHandbrakePosition(1f);
                for (int i = 0; i < 60; i++) yield return null;
                step.Result = Of(loco, w, ca, cb);
                yield break;
            }
            throw new Exception("no room to spawn the train");
        }

        /// <summary>The coupling between the loco and the wagon, as this game has it.</summary>
        [Step]
        public static CouplingState State(Coupling coupling)
        {
            var (ca, cb) = Ends(coupling);
            return new CouplingState
            {
                Coupled = ca.coupledTo == cb,
                SameSet = ca.train.trainset == cb.train.trainset,
                Hose = ca.GetAirHoseConnectedTo() == cb,
                Cocks = (ca.IsCockOpen ? "O" : "C") + (cb.IsCockOpen ? "O" : "C"),
                Chain = ca.state + "/" + cb.state,
                Fsm = (ca.ChainScript?.CurrentState.ToString() ?? "-") + "/" + (cb.ChainScript?.CurrentState.ToString() ?? "-"),
                Gap = (float)Math.Round(Vector3.Distance(ca.transform.position, cb.transform.position), 2),
                WagonAbs = Positions.Of(cb.train.transform.position),
                Speed = (float)Math.Round(Mathf.Abs(cb.train.GetForwardSpeed()), 2)
            };
        }

        /// <summary>
        /// This game's player uncouples by hand, a step every <paramref name="pause"/> seconds like a
        /// player: close both angle cocks, part the hoses, loosen the screw, then lift the chain off,
        /// carry it to its parking hook and let it go. Result: the steps, each with what it left.
        /// </summary>
        [Step]
        public static IEnumerator UncoupleByHand(Step<string[]> step, Coupling coupling, float pause)
        {
            var (ca, cb) = Ends(coupling);
            yield return LocalPlayer.TeleportToGround(Dev.Abs(ca.transform.position).x + 2.5f, Dev.Abs(ca.transform.position).z);
            var steps = new List<string>();
            var optimizers = LockChains(ca.train, cb.train);
            yield return WaitForChains(ca, cb, 15f);
            ca.IsCockOpen = false; cb.IsCockOpen = false; steps.Add("cocks closed"); yield return Pause(pause);
            ca.DisconnectAirHose(true); steps.Add("hoses parted: " + (ca.GetAirHoseConnectedTo() == null)); yield return Pause(pause);
            // The chain hanging on the hook belongs to one side; its screw button loosens it.
            var chain = new[] { ca.ChainScript, cb.ChainScript }.FirstOrDefault(c => c != null && c.CurrentState is ChainCouplerInteraction.State.Attached_Tight or ChainCouplerInteraction.State.Attached_Loose)
                        ?? throw new Exception("no chain on the hook: " + ca.ChainScript?.CurrentState + "/" + cb.ChainScript?.CurrentState);
            if (chain.CurrentState == ChainCouplerInteraction.State.Attached_Tight)
                chain.OnScrewButtonUsed();
            for (int i = 0; i < 30; i++) yield return null;
            steps.Add("loosened: " + chain.CurrentState); yield return Pause(pause);
            chain.OnGrabbed(null);
            for (int i = 0; i < 30; i++) yield return null;
            steps.Add("lifted: " + chain.CurrentState + ", coupled " + (ca.coupledTo != null));
            // Carry the chain to its parking hook, as a player does before letting go.
            for (int i = 0; i < 40; i++) { chain.knob.transform.position = chain.parkedAnchor.position; yield return null; }
            steps.Add("at park hook: ring " + Vector3.Distance(chain.chainRingAnchor.position, chain.parkedAnchor.position).ToString("F2") + " m away");
            chain.OnUngrabbed(null);
            for (int i = 0; i < 30; i++) yield return null;
            steps.Add("let go: " + chain.CurrentState + ", coupled " + (ca.coupledTo != null));
            foreach (var o in optimizers) o.UnlockLOD();
            step.Result = steps.ToArray();
        }

        /// <summary>
        /// This game's player couples by hand, a step every <paramref name="pause"/> seconds: lift a
        /// parked chain onto the other car's hook (a player who misses lets it dangle and tries
        /// again, three times at most), tighten the screw, join the hoses and open both angle cocks.
        /// Result: the steps.
        /// </summary>
        [Step]
        public static IEnumerator CoupleByHand(Step<string[]> step, Coupling coupling, float pause)
        {
            var (ca, cb) = Ends(coupling);
            yield return LocalPlayer.TeleportToGround(Dev.Abs(ca.transform.position).x + 2.5f, Dev.Abs(ca.transform.position).z);
            var optimizers = LockChains(ca.train, cb.train);
            yield return WaitForChains(ca, cb, 15f);
            var steps = new List<string>();
            var chain = ca.ChainScript; var other = cb.ChainScript;
            if (chain.CurrentState != ChainCouplerInteraction.State.Parked && chain.CurrentState != ChainCouplerInteraction.State.Dangling) { chain = cb.ChainScript; other = ca.ChainScript; }
            if (chain.CurrentState != ChainCouplerInteraction.State.Parked && chain.CurrentState != ChainCouplerInteraction.State.Dangling)
                throw new Exception("no free chain: " + ca.ChainScript.CurrentState + "/" + cb.ChainScript.CurrentState);
            for (int attempt = 0; attempt < 3 && chain.CurrentState is not (ChainCouplerInteraction.State.Attached_Loose or ChainCouplerInteraction.State.Attached_Tight); attempt++)
            {
                chain.OnGrabbed(null);
                for (int i = 0; i < 40; i++) { chain.knob.transform.position = other.ownAttachPoint.transform.position; yield return null; }
                chain.OnUngrabbed(null);
                for (int i = 0; i < 30; i++) yield return null;
                if (attempt > 0) steps.Add("hook attempt " + (attempt + 1));
            }
            steps.Add("hooked: " + chain.CurrentState + ", coupled " + (ca.coupledTo == cb)); yield return Pause(pause);
            if (chain.CurrentState == ChainCouplerInteraction.State.Attached_Loose)
                chain.OnScrewButtonUsed();
            for (int i = 0; i < 30; i++) yield return null;
            steps.Add("tightened: " + chain.CurrentState); yield return Pause(pause);
            ca.ConnectAirHose(cb, true); steps.Add("hoses joined: " + (ca.GetAirHoseConnectedTo() == cb)); yield return Pause(pause);
            ca.IsCockOpen = true; cb.IsCockOpen = true; steps.Add("cocks open");
            for (int i = 0; i < 30; i++) yield return null;
            foreach (var o in optimizers) o.UnlockLOD();
            step.Result = steps.ToArray();
        }

        /// <summary>Host: pulls the loco away from the wagon for 5 s and stops it. Result: whether
        /// the two are still in one trainset.</summary>
        [Step]
        public static IEnumerator PullAway(Step<bool> step, Coupling coupling)
        {
            var loco = Trains.Find(coupling.Loco); var wagon = Trains.Find(coupling.Wagon);
            foreach (var c in loco.trainset.cars) c.brakeSystem.SetHandbrakePosition(0f);
            if (loco.trainset == wagon.trainset) wagon.brakeSystem.SetHandbrakePosition(0f);
            for (float t0 = Time.time; Time.time - t0 < 5f;)
            {
                yield return new WaitForFixedUpdate();
                Vector3 away = (loco.transform.position - wagon.transform.position).normalized;
                Vector3 dir = loco.transform.forward * Mathf.Sign(Vector3.Dot(loco.transform.forward, away));
                loco.rb.AddForce(dir * 2f * Mathf.Clamp01((Time.time - t0) / 2f) - Vector3.Project(loco.rb.velocity, dir), ForceMode.VelocityChange);
            }
            for (int i = 0; i < 100; i++) { yield return new WaitForFixedUpdate(); foreach (var c in loco.trainset.cars) c.rb.velocity = Vector3.zero; }
            foreach (var c in loco.trainset.cars) c.brakeSystem.SetHandbrakePosition(1f);
            wagon.brakeSystem.SetHandbrakePosition(1f);
            step.Result = loco.trainset == wagon.trainset;
        }

        // ---- helpers for steps (in game)

        /// <summary>The coupling's two ends: the loco's coupler and the wagon's.</summary>
        internal static (Coupler loco, Coupler wagon) Ends(Coupling c)
        {
            TrainCar a = Trains.Find(c.Loco), b = Trains.Find(c.Wagon);
            return (c.LocoFront ? a.frontCoupler : a.rearCoupler, c.WagonFront ? b.frontCoupler : b.rearCoupler);
        }

        /// <summary>The pair of couplers, one on each car, nearest each other.</summary>
        internal static (Coupler a, Coupler b) Facing(TrainCar a, TrainCar b)
        {
            Coupler ca = null, cb = null;
            foreach (var x in new[] { a.frontCoupler, a.rearCoupler })
            foreach (var y in new[] { b.frontCoupler, b.rearCoupler })
                if (ca == null || Vector3.Distance(x.transform.position, y.transform.position) < Vector3.Distance(ca.transform.position, cb.transform.position)) { ca = x; cb = y; }
            return (ca, cb);
        }

        /// <summary>Chains only switch on when a camera renders them, and the test games are
        /// minimised: lock them on. Unlock what this returns when done.</summary>
        internal static List<ChainCouplerVisibilityOptimizer> LockChains(params TrainCar[] cars)
        {
            var optimizers = cars.SelectMany(c => c.GetComponentsInChildren<ChainCouplerVisibilityOptimizer>(true)).ToList();
            foreach (var o in optimizers) o.LockLOD();
            return optimizers;
        }

        /// <summary>Up to <paramref name="seconds"/> for both chains to switch on.</summary>
        internal static IEnumerator WaitForChains(Coupler ca, Coupler cb, float seconds)
        {
            for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < seconds && new[] { ca.ChainScript, cb.ChainScript }.Any(c => c == null || c.CurrentState == ChainCouplerInteraction.State.Disabled);)
                yield return null;
        }

        private static IEnumerator Pause(float seconds)
        {
            for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < seconds;) yield return null;
        }

        private static Coupling Of(TrainCar a, TrainCar b, Coupler ca, Coupler cb) => new Coupling
        {
            Loco = a.ID, Wagon = b.ID, LocoFront = ca.isFrontCoupler, WagonFront = cb.isFrontCoupler,
            Gap = (float)Math.Round(Vector3.Distance(ca.transform.position, cb.transform.position), 1),
            At = Positions.Of(ca.transform.position)
        };
    }
}
