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
    /// The host's own player works a coupling's chain and screw, with a client watching (the
    /// host-follower family, docs/design/host-local-session.md, stage 1). Before stage 1 the host's
    /// screw used in a state the mod didn't expect (CouplerChainInteractionPatch's Start fallback)
    /// went to the server through the host's own client, which applied it to the host's coupler a
    /// second time: the screw button stopped taking input and the coupler stayed marked in use.
    /// The host spawns a loco and a wagon, coupled, near its player, and the client comes to watch.
    /// The host uncouples them by hand and couples them again, then loosens and tightens the screw
    /// twice, and the mod's screw postfix runs on the other car's chain (the Start path). On both
    /// games: coupled, in one trainset, the chain tight, the same chain states, the hooked chain's
    /// screw button taking input and each knob taking input as vanilla has it in its state (off in
    /// Attached_Tight and Other_Attached*); on the host, neither car's end marked in use. A player
    /// host only: a dedicated host has no player to work the chain.
    /// </summary>
    [Scenario("host-chain-screw", "The host's own player uncouples, couples and works the screw: both games agree, every chain takes input as vanilla has it, and the host leaves no coupler marked in use")]
    [Needs(Need.PlayerHost)]
    [Budget(600)]
    public sealed class HostChainScrew : Scenario<HostChainScrew.Options>
    {
        public sealed class Options
        {
            public string Wagon = "BoxcarBrown";
            /// <summary>Seconds between hand steps.</summary>
            public float Pause = 1;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Coupling c = await pair.Host.Run<Coupling>(s => Couplings.SpawnCoupled(s, o.Wagon, "host"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, c.Cars)));
            Report.Note($"{c.Loco} and {c.Wagon} coupled near the host; the client comes to watch");
            await pair.Client.Run(() => Players.TeleportToGround(c.At.X - 3f, c.At.Z + 3f));
            await Task.Delay(2000);

            string[] steps = await pair.Host.Run<string[]>(s => Couplings.UncoupleByHand(s, c, o.Pause), 90);
            Report.Note("uncoupled by hand: " + string.Join("; ", steps));
            await Task.Delay(2000);
            steps = await pair.Host.Run<string[]>(s => Couplings.CoupleByHand(s, c, o.Pause), 90);
            Report.Note("coupled by hand: " + string.Join("; ", steps));
            await Task.Delay(2000);
            Screwed screwed = await pair.Host.Run<Screwed>(s => InGame.Screw(s, c), 90);
            Report.Note($"{screwed.Chain} chain: " + string.Join("; ", screwed.Steps));
            await Task.Delay(3000);

            PeerResults<Chains> chains = await pair.Both.Run<Chains>(s => InGame.Chains(s, c), 30);
            foreach (var (peer, r) in chains)
            {
                Report.Note($"{peer}: coupled {r.Coupled}, one set {r.SameSet}, chain {r.Chain} ({r.States}), screw/knob {r.Allowed} (S/K: takes input), {r.InUse}");
                Expect.That(r.Coupled && r.SameSet, $"{peer}: the cars are coupled", $"coupled {r.Coupled}, one set {r.SameSet}");
                Expect.That(r.Tight, $"{peer}: the chain is tight", r.Chain);
                Expect.Empty(r.Problems, $"{peer}: chains not taking input as vanilla has them");
            }
            Expect.Equal(chains.Host.Chain, chains.Client.Chain, "the client's chain states (the host's expected)");
            Expect.That(!chains.Host.InUse.Contains("in use"), "the host's server has no coupler marked in use", chains.Host.InUse);
        }

        public sealed class Screwed
        {
            public string[] Steps;
            public string Chain;
        }

        public sealed class Chains
        {
            public bool Coupled, SameSet, Tight;
            public string Chain, States, Allowed, InUse;
            public string[] Problems;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: the host's player works the screw. On the chain on the hook: loosen it and
            /// tighten it again, twice, through its screw button (vanilla's OnScrewButtonUsed, which
            /// the mod's postfix reports). Then the Start path: the postfix seeing the screw used in a
            /// state it doesn't expect, here on the other car's chain, which isn't on the hook. On the
            /// host that must change nothing: before stage 1 the server applied it to the host's own
            /// coupler, locked its screw button and left the coupler marked in use.
            /// </summary>
            [Step]
            public static IEnumerator Screw(Step<Screwed> step, Coupling coupling)
            {
                var (ca, cb) = Couplings.Ends(coupling);
                yield return LocalPlayer.TeleportToGround(Dev.Abs(ca.transform.position).x + 2.5f, Dev.Abs(ca.transform.position).z);
                var optimizers = Couplings.LockChains(ca.train, cb.train);
                yield return Couplings.WaitForChains(ca, cb, 15f);
                var steps = new List<string>();
                var hooked = new[] { ca.ChainScript, cb.ChainScript }.FirstOrDefault(c => c != null && c.CurrentState is ChainCouplerInteraction.State.Attached_Tight or ChainCouplerInteraction.State.Attached_Loose)
                             ?? throw new Exception("no chain on the hook: " + ca.ChainScript?.CurrentState + "/" + cb.ChainScript?.CurrentState);
                for (int round = 1; round <= 2; round++)
                {
                    foreach (var wanted in new[] { ChainCouplerInteraction.State.Attached_Loose, ChainCouplerInteraction.State.Attached_Tight })
                    {
                        if (hooked.CurrentState != wanted)
                            hooked.OnScrewButtonUsed();
                        for (float t0 = Time.realtimeSinceStartup; hooked.CurrentState != wanted && Time.realtimeSinceStartup - t0 < 5f;) yield return null;
                        steps.Add($"round {round}: {wanted} -> {hooked.CurrentState}");
                        for (int i = 0; i < 30; i++) yield return null;
                    }
                }
                // The Start path, as Harmony runs the postfix after the screw button's handler.
                var free = hooked == ca.ChainScript ? cb.ChainScript : ca.ChainScript;
                if (free != null)
                {
                    Multiplayer.Client.Trains.ChainCouplerInteractionPatch.OnScrewButtonUsed(free);
                    steps.Add("Start path on the other chain (" + free.CurrentState + ")");
                }
                else steps.Add("no second chain: Start path not exercised");
                for (int i = 0; i < 60; i++) yield return null;
                foreach (var o in optimizers) o.UnlockLOD();
                step.Result = new Screwed { Steps = steps.ToArray(), Chain = hooked == ca.ChainScript ? "loco's" : "wagon's" };
            }

            /// <summary>
            /// Either game: the coupling as this game has it, whether a player may use the chains as
            /// vanilla lets them, and on the host whether the server half of either car still has that
            /// end marked as being interacted with. Vanilla only gives the chain on the hook a screw
            /// button, and turns knobs off in Attached_Tight and in every Other_Attached state (the
            /// other car's chain while one is on the hook): screw and knob are checked against that,
            /// not against "everything takes input" (the 2026-10-20 run read the parked chain's knob,
            /// off as vanilla has it, as the bug). The bug this is after is the hooked chain's screw
            /// button left not taking input (the Start path's old fallback on the host). Allowed: S/s a
            /// screw button taking input or not, K/k the knob, - none.
            /// </summary>
            [Step]
            public static IEnumerator Chains(Step<Chains> step, Coupling coupling)
            {
                var (ca, cb) = Couplings.Ends(coupling);
                var optimizers = Couplings.LockChains(ca.train, cb.train);
                bool Enabled(Coupler c) => c.ChainScript != null && c.ChainScript.CurrentState != ChainCouplerInteraction.State.Disabled;
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 10f && !(Enabled(ca) && Enabled(cb));) yield return null;
                for (int i = 0; i < 30; i++) yield return null;   // settled into their states

                DV.CabControls.ButtonBase Screw(ChainCouplerInteraction chain) =>
                    chain.screwButton != null && chain.screwButton.activeInHierarchy ? chain.screwButton.GetComponent<DV.CabControls.ButtonBase>() : null;
                DV.CabControls.GizmoBase Knob(ChainCouplerInteraction chain) => chain.knobGizmo;
                bool Hooked(ChainCouplerInteraction chain) =>
                    chain.CurrentState is ChainCouplerInteraction.State.Attached_Tight or ChainCouplerInteraction.State.Attached_Loose;
                bool KnobExpected(ChainCouplerInteraction chain) =>
                    !(chain.CurrentState is ChainCouplerInteraction.State.Attached_Tight or ChainCouplerInteraction.State.Other_Attached
                      or ChainCouplerInteraction.State.Other_Attached_Parked or ChainCouplerInteraction.State.Other_Attached_Dangling);
                string Allowed(Coupler c)
                {
                    if (!Enabled(c)) return "--";
                    var screw = Screw(c.ChainScript); var knob = Knob(c.ChainScript);
                    return (screw == null ? "-" : screw.InteractionAllowed ? "S" : "s") + (knob == null ? "-" : knob.InteractionAllowed ? "K" : "k");
                }
                var problems = new List<string>();
                foreach (var (c, who) in new[] { (ca, "loco's"), (cb, "wagon's") })
                {
                    if (!Enabled(c)) { problems.Add($"the {who} chain never switched on"); continue; }
                    var chain = c.ChainScript;
                    var screw = Screw(chain); var knob = Knob(chain);
                    if (Hooked(chain) && (screw == null || !screw.InteractionAllowed))
                        problems.Add($"the {who} chain is on the hook ({chain.CurrentState}) and its screw button doesn't take input");
                    if (knob != null && knob.InteractionAllowed != KnobExpected(chain))
                        problems.Add($"the {who} chain's knob {(knob.InteractionAllowed ? "takes" : "doesn't take")} input in {chain.CurrentState}");
                }
                string InUse(Coupler c)
                {
                    var server = Multiplayer.Server.Trains.NetworkedTrainCarServer.Of(c.train.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>());
                    if (server == null) return "-";
                    return (c.isFrontCoupler ? server.frontInteracting : server.rearInteracting) ? "in use" : "free";
                }
                step.Result = new Chains
                {
                    Coupled = ca.coupledTo == cb,
                    SameSet = ca.train.trainset == cb.train.trainset,
                    Chain = ca.state + "/" + cb.state,
                    States = (ca.ChainScript?.CurrentState.ToString() ?? "none") + "/" + (cb.ChainScript?.CurrentState.ToString() ?? "none"),
                    Tight = ca.state == ChainCouplerInteraction.State.Attached_Tight || cb.state == ChainCouplerInteraction.State.Attached_Tight,
                    Allowed = Allowed(ca) + "/" + Allowed(cb),
                    Problems = problems.ToArray(),
                    InUse = InUse(ca) + "/" + InUse(cb)
                };
                foreach (var o in optimizers) o.UnlockLOD();
            }
        }
    }
}
