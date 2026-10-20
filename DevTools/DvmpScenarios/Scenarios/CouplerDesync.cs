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
    /// Coupler desync (#64): the client has a loco and wagon coupled that the host has apart, then the
    /// host pulls the loco away. The client steers each car to the host's position against a joint the
    /// host doesn't know about; when that joint couldn't break, the solver fought itself until the
    /// cars went wild. Pass: the client's cars never move or spin far beyond the host's, and both
    /// peers end up agreeing (uncoupled, cars where the host has them).
    /// </summary>
    [Scenario("coupler-desync", "The client has a loco and wagon coupled that the host has apart and the host pulls away: the client's cars never go wild, and both end up uncoupled with the wagon where the host has it")]
    [Budget(600)]
    public sealed class CouplerDesync : Scenario<CouplerDesync.Options>
    {
        public sealed class Options
        {
            /// <summary>The client's receive lag, ms.</summary>
            public int Lag;
            /// <summary>Seconds to watch.</summary>
            public int Watch = 20;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Coupling c = await pair.Host.Run<Coupling>(s => Couplings.SpawnApart(s, "BoxcarBrown", 0.4f), 90);
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, c.Cars)));
            await Task.Delay(3000);
            Watched hw, cw;
            long hostLog, clientLog;
            using (await pair.Client.Lag(o.Lag))
            {
                hostLog = await pair.Host.LogCursor();
                clientLog = await pair.Client.LogCursor();
                CouplingState gap = await pair.Host.Run(() => Couplings.State(c));
                Require.That(!gap.Coupled, "the setup left the cars apart on the host", gap.ToString());
                Desynced d = await pair.Client.Run<Desynced>(s => InGame.Desync(s, c), 30);
                Report.Note($"client coupled on its own: {d.Coupled} (one set {d.SameSet}, joint {d.Joint}, hose {d.Hose}; gap {gap.Gap} m on the host)");
                Require.That(d.Coupled, "the client coupled on its own");

                var watchers = pair.Both.Start<Watched>(s => InGame.Watch(s, c, o.Watch), o.Watch + 30);
                await pair.Host.Run<bool>(s => Couplings.PullAway(s, c), 60);
                PeerResults<Watched> watched = await watchers;
                (hw, cw) = (watched.Host, watched.Client);
            }
            await Task.Delay(3000);
            CouplingState h = await pair.Host.Run(() => Couplings.State(c));
            CouplingState k = await pair.Client.Run(() => Couplings.State(c));
            int Breaks(LogLine[] lines) => lines.Count(l => l.Message?.Contains("Coupler broke") == true);
            Report.Note($"coupler breaks logged: host {Breaks(await pair.Host.LogsSince(hostLog, "error"))}, client {Breaks(await pair.Client.LogsSince(clientLog, "error"))}");
            Report.Note($"host: max speed {hw.MaxSpeed} m/s, spin {hw.MaxSpin} deg/s, worst step {hw.WorstJump} m");
            Report.Note($"client: max speed {cw.MaxSpeed} m/s ({cw.MaxSpeedCar}), spin {cw.MaxSpin} deg/s, worst step {cw.WorstJump} m, couplers up to {cw.MaxGap} m apart");
            foreach (string e in cw.Events) Report.Note("client: " + e);

            Require.That(cw.CoupledAtStart, "the client was still coupled when the host pulled away (or nothing was tested)");
            Expect.AtMost(cw.MaxSpeed, hw.MaxSpeed + 3, "client cars' top speed (m/s; the host's plus 3)");
            Expect.AtMost(cw.MaxSpin, hw.MaxSpin + 45, "client cars' top spin (deg/s; the host's plus 45)");
            Expect.AtMost(cw.WorstJump, 0.5, "client car's worst single-step jump (m)");
            Expect.That(!(cw.Derailed && !hw.Derailed), "the client derailed only if the host did", $"host {hw.Derailed}, client {cw.Derailed}");
            float apart = Peers.Horizontal(h.WagonAbs, k.WagonAbs);
            Report.Note($"end: coupled host {h.Coupled} client {k.Coupled}, wagon {apart:0.00} m apart");
            Expect.That(h.Coupled == k.Coupled && h.SameSet == k.SameSet, "the peers agree whether the cars are coupled", $"host {h.Coupled}/{h.SameSet}, client {k.Coupled}/{k.SameSet}");
            Expect.AtMost(apart, 1, "wagon apart (m)");
        }

        public sealed class Desynced
        {
            public bool Coupled, SameSet, Joint, Hose;
        }

        public sealed class Watched
        {
            public float MaxSpeed, MaxSpin, WorstJump, MaxGap;
            public string MaxSpeedCar;
            public string[] Events;
            public bool CoupledAtStart, Coupled, Derailed;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Client: couples the loco and wagon here only, the way a bounce can leave a client
            /// coupled where the host isn't. Inside the authoritative-topology scope, so nothing is
            /// sent to the host (the same path a host snapshot takes). By the chain, without hoses: a
            /// coupling with hoses splits the brake line, which the host's brake state updates notice
            /// within a couple of seconds and resync, but a chain-only coupling leaves the brake lines
            /// alone and nothing else catches it.
            /// </summary>
            [Step]
            public static IEnumerator Desync(Step<Desynced> step, Coupling coupling)
            {
                var (ca, cb) = Couplings.Ends(coupling);
                using (Multiplayer.Shared.Trains.NetworkedTrainCar.BeginAuthoritativeTopologyApplication())
                    ca.CoupleTo(cb, false, true);
                for (int i = 0; i < 30; i++) yield return new WaitForFixedUpdate();
                step.Result = new Desynced { Coupled = ca.coupledTo == cb, SameSet = ca.train.trainset == cb.train.trainset, Joint = ca.rigidCJ != null || cb.rigidCJ != null, Hose = ca.GetAirHoseConnectedTo() == cb };
            }

            /// <summary>Either peer: watches the loco and wagon every physics step: the fastest either
            /// moved or spun, the biggest single-step jump, and when the coupling between them changed.
            /// A desynced joint fighting the corrections shows as speeds and spins far beyond what the
            /// host's train does.</summary>
            [Step]
            public static IEnumerator Watch(Step<Watched> step, Coupling coupling, float seconds)
            {
                var (ca, cb) = Couplings.Ends(coupling);
                var cars = new[] { ca.train, cb.train };
                float maxSpeed = 0, maxSpin = 0, worstJump = 0, maxGap = 0;
                string maxSpeedCar = "";
                var events = new List<string>();
                var last = cars.Select(c => Dev.Abs(c.transform.position)).ToArray();
                bool coupled = ca.coupledTo == cb, coupledAtStart = coupled;
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < seconds)
                {
                    yield return new WaitForFixedUpdate();
                    float t = Time.realtimeSinceStartup - t0;
                    for (int i = 0; i < cars.Length; i++)
                    {
                        var c = cars[i];
                        float v = c.rb.velocity.magnitude, w = c.rb.angularVelocity.magnitude * Mathf.Rad2Deg;
                        if (v > maxSpeed) { maxSpeed = v; maxSpeedCar = c.ID + " at " + t.ToString("F1") + "s"; }
                        maxSpin = Mathf.Max(maxSpin, w);
                        Vector3 p = Dev.Abs(c.transform.position);
                        worstJump = Mathf.Max(worstJump, Vector3.Distance(p, last[i]));
                        last[i] = p;
                        if (c.derailed && events.Count < 20 && !events.Any(e => e.Contains(c.ID + " derailed")))
                            events.Add($"{t:F1}s {c.ID} derailed");
                    }
                    maxGap = Mathf.Max(maxGap, Vector3.Distance(ca.transform.position, cb.transform.position));
                    bool now = ca.coupledTo == cb;
                    if (now != coupled && events.Count < 20)
                        events.Add($"{t:F1}s {(now ? "coupled" : "uncoupled")}, couplers {Vector3.Distance(ca.transform.position, cb.transform.position):F2} m apart");
                    coupled = now;
                }
                step.Result = new Watched
                {
                    MaxSpeed = (float)Math.Round(maxSpeed, 2), MaxSpeedCar = maxSpeedCar, MaxSpin = (float)Math.Round(maxSpin, 1),
                    WorstJump = (float)Math.Round(worstJump, 2), MaxGap = (float)Math.Round(maxGap, 2), Events = events.ToArray(),
                    CoupledAtStart = coupledAtStart, Coupled = coupled, Derailed = cars.Any(c => c.derailed)
                };
            }
        }
    }
}
