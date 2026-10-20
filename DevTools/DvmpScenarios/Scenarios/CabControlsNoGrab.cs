using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// An S282A, both players in its cab (#159). The client sets the valves (compressor, dynamo,
    /// lubricator, cylinder cocks) with no grab, again and again on the one loco, now the cab's
    /// copies, now the outside ones: every change reaches the host. Then the client steps off, out of
    /// reach, and turns the outside valves: the host keeps its value and the client's go back to it
    /// (left turned, they showed only on the client, for good). Back aboard, its next change reaches
    /// the host again. The client must still be aboard after the steps: a rolling loco left it behind
    /// in #159. A dedicated host works too (only the client boards). Was cab-controls
    /// MODE=no-grab-sequence.
    /// </summary>
    [Scenario("cab-controls-no-grab", "The client sets an S282A's valves with no grab, cab and outside copies in turn: every change reaches the host; out of reach its change goes back to the host's; back aboard it reaches the host again")]
    [Budget(600)]
    public sealed class CabControlsNoGrab : Scenario<CabControlsNoGrab.Options>
    {
        public sealed class Options
        {
            /// <summary>"which value;...": which copies the client sets (cab or external) and to what.</summary>
            public string Steps = "external 1;external 0;external 1;cab 0;cab 1;external 0;cab 1;cab 0;external 1;external 0";
        }

        private static readonly string[] Ports = { "compressorControl.EXT_IN", "dynamoControl.EXT_IN", "lubricatorControl.EXT_IN", "cylinderCock.EXT_IN" };

        protected override async Task Run(Pair pair, Options o)
        {
            HeldLoco loco = await pair.Host.Run<HeldLoco>(s => Cars.SpawnHeldLoco(s, "LocoS282A"), 150);
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            pair.Cleanup(async () =>
            {
                await pair.Client.Run(() => Players.TeleportToGround(7190f, 6880f));
                if (!pair.Dedicated) await pair.Host.Run(() => Players.TeleportToGround(7194f, 6873f));
            });
            // A dedicated host has no player to board.
            if (!pair.Dedicated)
            {
                await pair.Host.Run(() => Players.Board(loco.Loco));
                await Task.Delay(2000);   // the cab settles after boarding, as before
            }
            await pair.Client.Run(() => Players.Board(loco.Loco));
            await Task.Delay(4000);

            // The client sets one copy of each valve, and the host's values must match.
            async Task<Valved> Valve(string which, float value)
            {
                Valved v = await pair.Client.Run<Valved>(s => InGame.Valve(s, loco.Loco, which, Ports, value), 30);
                await Task.Delay(1000);   // the host applies it, as before
                return v;
            }
            float last = 0;
            foreach (string[] step in o.Steps.Split(';').Select(s => s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)))
            {
                string which = step[0];
                float value = float.Parse(step[1], CultureInfo.InvariantCulture);
                Valved v = await Valve(which, value);
                await Cabs.ExpectHostAgrees(pair, loco.Loco, v.Values, $"the {which} copies set to {value}");
                last = value;
            }
            // The steps are only the in-reach case while the client is still aboard (#159's ran on a
            // client left behind by its rolling loco).
            Reach r = await pair.Client.Run(() => InGame.Reach(loco.Loco));
            Expect.That(r.Distance <= r.Within, "the client is still aboard", $"{r.Distance} m from the loco (reach {r.Within} m)");

            Report.Note("== the client steps off, out of reach");
            Away away = await pair.Client.Run<Away>(s => InGame.StepOff(s, loco.Loco), 60);
            Report.Note($"{away.Distance} m from the loco (reach {away.Within} m), on the car {away.OnCar}, cab loaded {away.Cab}, outside controls loaded {away.External}");
            string where = away.External ? "external" : "cab";
            float turned = 1 - last;
            Valved off = await Valve(where, turned);
            await Cabs.ExpectHostAgrees(pair, loco.Loco, off.Values, $"out of reach, the {where} copies set to {turned}: back to the host's");
            HostPorts host = await pair.Host.Run(() => Cabs.Host(loco.Loco, Ports));
            var took = host.Values.Where(v => Math.Abs(v.Value - last) > 0.01).ToList();
            Expect.That(took.Count == 0, $"host: kept {last}", took.Count == 0 ? "yes" : "took the change: " + string.Join(", ", took));

            Report.Note("== back aboard");
            await pair.Client.Run(() => Players.Board(loco.Loco));
            await Task.Delay(4000);
            Valved back = await Valve(where, turned);
            await Cabs.ExpectHostAgrees(pair, loco.Loco, back.Values, $"the {where} copies set to {turned}");

            Abs now = await pair.Host.Run(() => Cars.Position(loco.Loco));
            Report.Note($"the loco moved {Peers.Distance(now, loco.At):0.##} m");
        }

        public sealed class Valved
        {
            /// <summary>"port@where:control" for each control set.</summary>
            public string[] Set;
            /// <summary>"port=where,where" for each port: every copy and where it is.</summary>
            public string[] Copies;
            public PortValue[] Values;
        }

        public sealed class Reach
        {
            public float Distance, Within;
        }

        public sealed class Away
        {
            public float Distance, Within;
            public bool OnCar, Cab, External;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Client: sets ONE copy of each port's control, the cab's (<paramref name="which"/>=cab)
            /// or the outside one (external: the car's external interactables, not their dummy),
            /// straight to a value with no grab. Result: what it set, how many copies each port has
            /// where, and the client's port values once settled.
            /// </summary>
            [Step]
            public static IEnumerator Valve(Step<Valved> step, string loco, string which, string[] ports, float value)
            {
                TrainCar car = Trains.Find(loco);
                var dummy = car.loadedDummyExternalInteractables;
                IEnumerable<(DV.CabControls.ControlImplBase control, string where, string port)> Under(GameObject root, string where) =>
                    root == null ? Enumerable.Empty<(DV.CabControls.ControlImplBase, string, string)>()
                    : root.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true)
                        .Select(c => (c, dummy != null && c.transform.IsChildOf(dummy.transform) ? "dummy" : where,
                                      c.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>()?.portId))
                        .Where(x => x.Item3 != null && ports.Contains(x.Item3));
                var all = Under(car.loadedInterior, "cab").Concat(Under(car.loadedExternalInteractables, "external")).Concat(Under(dummy, "dummy")).ToList();
                string[] copies = ports.Select(p => p + "=" + string.Join(",", all.Where(x => x.port == p).Select(x => x.where))).ToArray();
                var chosen = ports.Select(p => all.FirstOrDefault(x => x.port == p && x.where == which)).Where(x => x.control != null).ToList();
                if (chosen.Count == 0) throw new Exception("no " + which + " control feeds " + string.Join(",", ports) + "; copies: " + string.Join("; ", copies));
                // Named now: a control can be unloaded by the time the values are read (the player moving off).
                string[] set = chosen.Select(x => x.port + "@" + x.where + ":" + x.control.name).ToArray();
                foreach (var x in chosen)
                {
                    if (x.control == null)
                        throw new Exception(x.port + "'s " + x.where + " control was unloaded before it could be set");
                    x.control.SetValue(value);
                    yield return new WaitForSecondsRealtime(0.2f);
                }
                yield return new WaitForSecondsRealtime(2.5f);   // settling, as before
                step.Result = new Valved { Set = set, Copies = copies, Values = Cabs.Read(car, chosen.Select(x => x.port)) };
            }

            /// <summary>Client: how far the player is from the loco, and the reach the host checks (a
            /// car's length from its centre).</summary>
            [Step]
            public static Reach Reach(string loco)
            {
                TrainCar car = Trains.Find(loco);
                return new Reach
                {
                    Distance = (float)Math.Round(Vector3.Distance(PlayerManager.PlayerTransform.position, car.transform.position), 1),
                    Within = (float)Math.Round(CarSpawner.Instance.carLiveryToCarLength[car.carLivery], 1)
                };
            }

            /// <summary>
            /// Client: steps off the loco onto the ground beside it, just further from it than the
            /// reach the host checks: where a client left behind by a rolling loco was (#159). Only
            /// just: at 26 m the outside controls unloaded under the next step, and the cab already had
            /// at 19.6 m. Result: how far it is and what's loaded.
            /// </summary>
            [Step]
            public static IEnumerator StepOff(Step<Away> step, string loco)
            {
                TrainCar car = Trains.Find(loco);
                float reach = CarSpawner.Instance.carLiveryToCarLength[car.carLivery];
                Vector3 side = Vector3.ProjectOnPlane(car.transform.right, Vector3.up).normalized;
                Vector3 to = Dev.Abs(car.transform.position + side * (reach + 1.5f));
                yield return LocalPlayer.TeleportToGround(to.x, to.z);
                for (int i = 0; i < 30; i++) yield return null;
                step.Result = new Away
                {
                    Distance = (float)Math.Round(Vector3.Distance(PlayerManager.PlayerTransform.position, car.transform.position), 1),
                    Within = (float)Math.Round(reach, 1),
                    OnCar = PlayerManager.Car == car,
                    Cab = car.loadedInterior != null,
                    External = car.loadedExternalInteractables != null
                };
            }
        }
    }
}
