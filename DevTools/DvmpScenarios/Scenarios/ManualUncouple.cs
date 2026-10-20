using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Manual decoupling by a client (upstream #166): close both angle cocks, part the hoses, loosen
    /// the screw, lift the chain onto its parking hook. Both peers must see the cars uncoupled and the
    /// chains parked, and when the host drives the loco away the wagon must stay. Reported to fail a
    /// third to half of the time, so it repeats. MODE=recouple then couples the cars again by hand
    /// (hook the chain, tighten, hoses, cocks), and the wagon must follow the loco instead.
    /// </summary>
    [Scenario("manual-uncouple", "The client uncouples by hand (cocks, hoses, screw, chain): both peers agree and the wagon stays when the host drives off; MODE=recouple couples again by hand and the wagon must follow")]
    [Budget(900)]
    public sealed class ManualUncouple : Scenario<ManualUncouple.Options>
    {
        public sealed class Options
        {
            /// <summary>Runs.</summary>
            public int N = 4;
            /// <summary>Seconds between hand steps.</summary>
            public float Pause = 1;
            /// <summary>The client's receive lag, ms.</summary>
            public int Lag;
            public string Wagon = "BoxcarBrown";
            /// <summary>uncouple, or recouple: uncouple and couple again.</summary>
            public string Mode = "uncouple";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            bool recouple = o.Mode == "recouple" ? true : o.Mode == "uncouple" ? false : throw new ArgumentException("MODE is uncouple or recouple");
            using (await pair.Client.Lag(o.Lag))
            {
                for (int run = 1; run <= o.N; run++)
                {
                    Coupling c = await pair.Host.Run<Coupling>(s => Couplings.SpawnCoupled(s, o.Wagon, "client"));
                    bool deleted = false;
                    Func<Task> delete = async () =>
                    {
                        if (deleted) return;
                        deleted = true;
                        await pair.Host.Run<string[]>(s => Cars.Delete(s, c.Cars));
                    };
                    pair.Cleanup(delete);
                    string label = $"run {run} ({c.Loco} + {c.Wagon})";
                    await Task.Delay(2000);

                    // A hand step that throws (no chain on the hook...) fails this run, and the next run goes on.
                    string[] steps;
                    try
                    {
                        steps = await pair.Client.Run<string[]>(s => Couplings.UncoupleByHand(s, c, o.Pause), 90);
                        if (recouple)
                        {
                            await Task.Delay(2000);
                            steps = await pair.Client.Run<string[]>(s => Couplings.CoupleByHand(s, c, o.Pause), 90);
                        }
                    }
                    catch (StepFailed e)
                    {
                        Expect.That(false, $"{label}: the client's hand steps", e.Message);
                        await delete();
                        continue;
                    }
                    await Task.Delay(3000);
                    PeerResults<CouplingState> before = await pair.Both.Run(() => Couplings.State(c));
                    await pair.Host.Run(() => InGame.DriveAway(c.Loco, c.Wagon));
                    await Task.Delay(2000);
                    PeerResults<CouplingState> after = await pair.Both.Run(() => Couplings.State(c));

                    int failed = 0;
                    foreach (var (peer, x, y) in new[] { ("host", before.Host, after.Host), ("client", before.Client, after.Client) })
                    {
                        float moved = Peers.Horizontal(x.WagonAbs, y.WagonAbs);
                        if (recouple)
                        {
                            if (!Expect.That(x.Coupled && x.SameSet && x.Hose && x.Cocks == "OO", $"{label}: {peer} fully coupled", $"chain {x.Chain}, hose {x.Hose}, cocks {x.Cocks}")) failed++;
                            if (!Expect.AtLeast(moved, 5, $"{label}: {peer} wagon followed the loco (m)")) failed++;
                        }
                        else
                        {
                            if (!Expect.That(!x.Coupled && !x.SameSet, $"{label}: {peer} uncoupled", $"coupled {x.Coupled}, one set {x.SameSet} ({x.Chain})")) failed++;
                            if (!Expect.That(x.Chain.StartsWith("Parked/") || x.Chain.EndsWith("/Parked"), $"{label}: {peer} chain parked", x.Chain)) failed++;
                            if (!Expect.AtMost(moved, 0.5, $"{label}: {peer} wagon followed the loco (m)")) failed++;
                        }
                    }
                    Report.Note(recouple
                        ? $"{label}: coupled, chain {before.Host.Chain}, wagon followed {Peers.Horizontal(before.Host.WagonAbs, after.Host.WagonAbs):0.0} m"
                        : $"{label}: chains {before.Host.Chain}, loco pulled away {after.Host.Gap} m");
                    if (failed > 0) Report.Note("client steps: " + string.Join("; ", steps));
                    // Clear the pair away: a later pair spawns on the same track.
                    await delete();
                }
            }
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: drives the loco away from the wagon (no air, so pushes it), the wagon
            /// braked: pushing into it shoves it along, or couples again on contact.</summary>
            [Step]
            public static IEnumerator DriveAway(string loco, string wagon)
            {
                var w = Trains.Find(wagon);
                w.brakeSystem.SetHandbrakePosition(1f);
                var l = Trains.Find(loco);
                l.brakeSystem.SetHandbrakePosition(0f);
                float away = Vector3.Dot(l.transform.forward, w.transform.position - l.transform.position) > 0 ? -3f : 3f;
                yield return Trains.Push(l, away, 3f, 1f);
                l.brakeSystem.SetHandbrakePosition(1f);
            }
        }
    }
}
