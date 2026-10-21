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
    /// A rejoin after an auto-couple on spawn (#285). The host spawns a loco beside a parked car
    /// and vanilla's spawn auto-couples them. The parked car's chain is switched on, as when a
    /// player is looking at it: vanilla then hooks that chain on as well, so both couplers of the
    /// one coupling have their chain attached, and the joint is on the new loco's. Both peers must
    /// agree on the coupling (coupled, one trainset, hoses, cocks, both chains). The client then
    /// quits and rejoins: the train baseline must apply, and the peers must still agree. On beta
    /// the rejoin failed every time: the client couldn't give the coupler without the joint an
    /// attached chain, so the baseline's postcondition check refused it.
    /// </summary>
    [Scenario("rejoin-autocouple", "A loco spawned beside a parked car auto-couples to it: both peers agree on the coupling, and after a client rejoin the train baseline applies and they still agree")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class RejoinAutocouple : Scenario<RejoinAutocouple.Options>
    {
        public sealed class Options
        {
            /// <summary>The car that is already standing there.</summary>
            public string Parked = "LocoDE2";
            /// <summary>The loco spawned beside it.</summary>
            public string Loco = "LocoDE2";
            /// <summary>Switch the parked car's chain on before the loco couples (the case that broke).
            /// 0: leave it as the minimised game has it, so only the joint's chain is attached.</summary>
            public bool ParkedChain = true;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Coupling c = await pair.Host.Run<Coupling>(s => InGame.SpawnBeside(s, o.Parked, o.Loco, o.ParkedChain), 120);
            bool deleted = false;
            Func<Task> delete = async () =>
            {
                if (deleted) return;
                deleted = true;
                await pair.Host.Run<string[]>(s => Cars.Delete(s, c.Cars));
            };
            pair.Cleanup(delete);
            await Task.Delay(3000);

            CouplingState host = await pair.Host.Run(() => Couplings.State(c));
            Report.Note($"{c.Loco} spawned beside {c.Wagon}: {host}, chain FSM {host.Fsm}");
            Expect.That(host.Coupled && host.SameSet, "the host auto-coupled the spawned loco", host.ToString());
            await Agree(pair, c, "after the auto-couple");

            await pair.Client.Quit();
            await Task.Delay(4000);   // the host notices the departure
            await pair.Rejoin();
            await pair.Client.Run(() => Rejoin.InGame.Settle(5f));
            await Agree(pair, c, "after the rejoin");
        }

        /// <summary>Both peers have the coupling the same: coupled, one set, hoses, cocks and both chains.</summary>
        private static async Task<bool> Agree(Pair pair, Coupling c, string label)
        {
            PeerResults<CouplingState> s = await pair.Both.Run(() => Couplings.State(c));
            bool ok = Expect.Equal(s.Host.Coupled, s.Client.Coupled, $"{label}: coupled on both");
            ok &= Expect.Equal(s.Host.SameSet, s.Client.SameSet, $"{label}: one trainset on both");
            ok &= Expect.Equal(s.Host.Hose, s.Client.Hose, $"{label}: hoses on both");
            ok &= Expect.Equal(s.Host.Cocks, s.Client.Cocks, $"{label}: cocks (loco/parked) on both");
            ok &= Expect.Equal(s.Host.Chain, s.Client.Chain, $"{label}: chains (loco/parked) on both");
            if (!ok) Report.Note($"{label}: host {s.Host}; client {s.Client}");
            return ok;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: a <paramref name="parked"/> car on a track near the client's player, handbrake on,
            /// then a <paramref name="loco"/> spawned just beyond one of its ends (vanilla's
            /// SpawnCarOnClosestTrack, which auto-couples it as it lands). With
            /// <paramref name="parkedChain"/>, the parked car's chains are switched on first. Tries
            /// other tracks until the loco couples.
            /// </summary>
            [Step]
            public static IEnumerator SpawnBeside(Step<Coupling> step, string parked, string loco, bool parkedChain)
            {
                var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First();
                var tried = new List<string>();
                foreach (float d in new[] { 20f, 40f, 60f, 80f })
                foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
                {
                    var t = CarSpawner.GetTrackClosestTo(client.transform.position + dir * d, 60f, out int _);
                    if (t == null) continue;
                    var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<DV.ThingTypes.TrainCarLivery> { Cars.Livery(parked) },
                        new List<bool> { false }, t, false, true);
                    if (cars == null || cars.Count == 0) continue;
                    Cars.Record(cars);
                    TrainCar standing = cars[0];
                    standing.brakeSystem.SetHandbrakePosition(1f);
                    for (int i = 0; i < 90; i++) yield return null;
                    if (parkedChain)
                    {
                        Couplings.LockChains(standing);
                        yield return Couplings.WaitForChains(standing.frontCoupler, standing.rearCoupler, 15f);
                    }

                    var livery = Cars.Livery(loco);
                    float half = CarSpawner.GetBoundsOfCar(livery.prefab).extents.z;
                    foreach (Coupler end in new[] { standing.frontCoupler, standing.rearCoupler })
                    {
                        Vector3 outward = (end.transform.position - standing.transform.position).normalized;
                        TrainCar spawned = CarSpawner.Instance.SpawnCarOnClosestTrack(end.transform.position + outward * (half + 0.3f), livery, false, false, false);
                        if (spawned == null) { tried.Add($"{t.name} {(end.isFrontCoupler ? "front" : "rear")}: no room"); continue; }
                        Cars.Record(new[] { spawned });
                        for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 3f && spawned.trainset != standing.trainset;)
                            yield return null;
                        if (spawned.trainset != standing.trainset)
                        {
                            var (sa, sb) = Couplings.Facing(spawned, standing);
                            tried.Add($"{t.name} {(end.isFrontCoupler ? "front" : "rear")}: not coupled, gap {Vector3.Distance(sa.transform.position, sb.transform.position):F2} m");
                            Trains.Delete(spawned);
                            for (int i = 0; i < 30; i++) yield return null;
                            continue;
                        }
                        spawned.brakeSystem.SetHandbrakePosition(1f);
                        var (ca, cb) = Couplings.Facing(spawned, standing);
                        if (tried.Count > 0) step.Note("tried: " + string.Join("; ", tried));
                        step.Note($"joint on {(ca.rigidCJ != null ? "the spawned loco" : "the parked car")}; chains {ca.state}/{cb.state}, FSM {ca.ChainScript?.CurrentState.ToString() ?? "-"}/{cb.ChainScript?.CurrentState.ToString() ?? "-"}");
                        step.Result = new Coupling
                        {
                            Loco = spawned.ID, Wagon = standing.ID, LocoFront = ca.isFrontCoupler, WagonFront = cb.isFrontCoupler,
                            Gap = (float)Math.Round(Vector3.Distance(ca.transform.position, cb.transform.position), 1),
                            At = Positions.Of(ca.transform.position)
                        };
                        yield break;
                    }
                    Trains.Delete(standing);
                    for (int i = 0; i < 30; i++) yield return null;
                }
                throw new Exception("no spawned loco coupled to a parked car: " + string.Join("; ", tried));
            }
        }
    }
}
