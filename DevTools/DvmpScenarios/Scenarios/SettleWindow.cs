using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A client's control change made just after a host resync still reaches the host (playtest 2:
    /// closed oiling caps on a 2901 reopened 10-15 s later). A client takes a control's change as the
    /// player's only once the host's last value for it has settled (1.5 s), so it isn't the host's
    /// own value echoing back. A change inside that window was dropped for good, and every game that
    /// loaded the car's interior asked for a full resync, which restarts the window for every port:
    /// a player at the loading distance reloaded it every few seconds.
    /// 1. The client asks for a resync and, 0.3 s after it lands, sets a car-body control with no
    ///    grab (an oiling cap by default). The host must get the change, and it must survive the
    ///    next resync.
    /// 2. The client's game reloads the car's interior RELOADS times, a second apart. It may ask for
    ///    at most one resync per car per 10 s: no more than 2 requests in 12 s.
    /// A dedicated host works too.
    /// </summary>
    [Scenario("settle-window", "A client's control change 0.3 s after a host resync reaches the host and survives the next resync; interior reloads ask for at most 2 resyncs in 12 s")]
    [Budget(300)]
    public sealed class SettleWindow : Scenario<SettleWindow.Options>
    {
        public sealed class Options
        {
            public string Loco = "LocoS282A";
            /// <summary>A port id or part of one (LOCO=LocoDE6 PORT=windowL1 for a cab window).</summary>
            public string Port = "oil";
            public int Reloads = 4;
        }

        private const string SyncRequest = "ServerboundTrainSyncRequestPacket";

        protected override async Task Run(Pair pair, Options o)
        {
            Loco loco = await pair.Host.Run<Loco>(s => Cars.SpawnLoco(s, o.Loco, true, false));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            pair.Cleanup(() => pair.Client.Run(() => Players.TeleportToGround(7190f, 6880f)));
            Report.Note($"== {o.Loco} {loco.Id}; the client boards it");
            await pair.Client.Run(() => Players.Board(loco.Id));
            await Task.Delay(4000);   // the cab settles after boarding, as before

            Report.Note("== the client sets a control 0.3 s after a resync lands");
            Changed s = await pair.Client.Run<Changed>(st => InGame.SetAfterResync(st, loco.Id, o.Port, 0.3f), 30);
            string port = s.Port;
            float host = (await pair.Host.Run(() => Cabs.Values(loco.Id, new[] { port })))[0].Value;
            Report.Note($"{port} set to {s.Value} {s.Since} s after the resync: client {s.Client}, host {host}");
            Require.That(Math.Abs(host - s.Value) < 0.02, $"the host got the client's change to {port}", $"host {host}, client set {s.Value}");

            Report.Note("== another resync");
            await pair.Client.Run(() => InGame.Resync(loco.Id));
            float client = (await pair.Client.Run(() => Cabs.Values(loco.Id, new[] { port })))[0].Value;
            host = (await pair.Host.Run(() => Cabs.Values(loco.Id, new[] { port })))[0].Value;
            Require.That(Math.Abs(client - s.Value) < 0.02 && Math.Abs(host - s.Value) < 0.02,
                $"{port} stayed at {s.Value} after the next resync", $"client {client}, host {host}");

            Report.Note($"== the client's game reloads the interior {o.Reloads} times");
            // Off the car, next to it: unloading the cab with the player in it would drop them through its floor.
            await pair.Client.Run(() => InGame.StepBeside(loco.Id));
            await pair.Host.Run(() => Received.Count(new[] { SyncRequest }));
            Reloaded r = await pair.Client.Run<Reloaded>(st => InGame.Reload(st, loco.Id, o.Reloads), 40);
            int n = (await pair.Host.Run(() => Received.Counts(new[] { SyncRequest }))).Where(c => c.From == "client" && c.Car == loco.NetId).Sum(c => c.N);
            Report.Note($"resync requests for car {loco.NetId} in 12 s: {n} (interior loaded at the end: {r.Loaded})");
            Expect.AtMost(n, 2, $"resync requests from {o.Reloads} interior reloads in 12 s");
            Expect.AtLeast(n, 1, "resync requests from the interior reloads");
        }

        public sealed class Changed
        {
            public string Port;
            public float Value, Since, Client;
        }

        public sealed class Reloaded
        {
            public bool Loaded;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Client: asks the host for a full resync of the car, and <paramref name="delay"/> s after
            /// it lands (the host's value for the port has just been received) sets the car-body control
            /// that feeds the port, with no grab: what a VR hand closing an oiling cap does. The change
            /// falls in the 1.5 s the client waits for the host's values to settle. <paramref name="port"/>
            /// is a port id, or part of one: the first car-body control whose port contains it. The
            /// value is the other end from where it is. Result: the port, the value set, how long after
            /// the resync, and the client's value 3 s on.
            /// </summary>
            [Step]
            public static IEnumerator SetAfterResync(Step<Changed> step, string loco, string port, float delay)
            {
                TrainCar car = Trains.Find(loco);
                var ntc = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>();
                var part = Multiplayer.Client.Trains.NetworkedTrainCarClient.Of(ntc);
                // Car-body controls are on the external interactables, which vanilla instantiates under
                // the car's interior transform, not under the car: wait for them to load (the player is
                // on the car), then look there as well as on the car itself.
                for (float t0 = Time.realtimeSinceStartup; !car.AreExternalInteractablesLoaded && Time.realtimeSinceStartup - t0 < 10f;)
                    yield return null;
                var feeders = new[] { car.transform, car.interior }.Where(root => root != null).Distinct()
                    .SelectMany(root => root.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true)).Distinct()
                    .Select(c => (control: c, port: c.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>()?.portId))
                    .Where(x => !string.IsNullOrEmpty(x.port)).ToList();
                var pick = feeders.FirstOrDefault(x => x.port == port);
                if (pick.control == null) pick = feeders.FirstOrDefault(x => x.port.IndexOf(port, StringComparison.OrdinalIgnoreCase) >= 0);
                if (pick.control == null) throw new Exception("no car-body control feeds a port like '" + port + "'; car-body ports: " + string.Join(", ", feeders.Select(x => x.port).Distinct()));
                if (!ntc.simulationFlow.TryGetPort(pick.port, out var p)) throw new Exception("no port " + pick.port);
                uint portNetId = Multiplayer.Shared.Trains.NetworkedTrainCar.GetPortNetId(pick.port);
                float value = p.Value < 0.5f ? 1f : 0f;

                float before = part.hostPortReceivedAt.TryGetValue(portNetId, out float t) ? t : 0f;
                AskResync(part);
                float asked = Time.realtimeSinceStartup;
                while ((!part.hostPortReceivedAt.TryGetValue(portNetId, out t) || t <= before) && Time.realtimeSinceStartup - asked < 10f)
                    yield return null;
                if (t <= before) throw new Exception("the resync never arrived");
                yield return new WaitForSecondsRealtime(delay);   // into the settle window: the condition under test
                float since = Time.realtimeSinceStartup - t;
                pick.control.SetValue(value);
                yield return new WaitForSecondsRealtime(3f);   // past the window, as before
                step.Result = new Changed { Port = pick.port, Value = value, Since = (float)Math.Round(since, 2), Client = (float)Math.Round(p.Value, 3) };
            }

            /// <summary>Client: asks the host for a full resync of the car, and gives it 3 s.</summary>
            [Step]
            public static IEnumerator Resync(string loco)
            {
                AskResync(Multiplayer.Client.Trains.NetworkedTrainCarClient.Of(Trains.Find(loco).GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>()));
                yield return new WaitForSecondsRealtime(3f);
            }

            /// <summary>Client: onto the ground 4 m to the loco's right.</summary>
            [Step]
            public static IEnumerator StepBeside(string loco)
            {
                TrainCar car = Trains.Find(loco);
                Vector3 to = Dev.Abs(car.transform.position + car.transform.right * 4f);
                yield return LocalPlayer.TeleportToGround(to.x, to.z);
            }

            /// <summary>Client: unloads and loads the car's interior <paramref name="reloads"/> times, a
            /// second apart, then waits until 12 s have passed since the first.</summary>
            [Step]
            public static IEnumerator Reload(Step<Reloaded> step, string loco, int reloads)
            {
                TrainCar car = Trains.Find(loco);
                float t0 = Time.realtimeSinceStartup;
                for (int i = 0; i < reloads; i++)
                {
                    car.UnloadInterior();
                    yield return null;
                    car.LoadInterior();
                    yield return new WaitForSecondsRealtime(1f);   // the condition under test
                }
                while (Time.realtimeSinceStartup - t0 < 12f) yield return null;
                step.Result = new Reloaded { Loaded = car.IsInteriorLoaded };
            }

            // A full resync now, through the car's own request (what loading its interior does), past
            // its once-per-10-s limit and leaving that limit as it was: the bash scenario called
            // SendTrainSyncRequest, which doesn't touch it, and part 2 measures it.
            private static void AskResync(Multiplayer.Client.Trains.NetworkedTrainCarClient part)
            {
                float next = part.nextInteriorResyncAt;
                bool pending = part.interiorResyncPending;
                part.nextInteriorResyncAt = 0;
                part.RequestInteriorResync();
                part.nextInteriorResyncAt = next;
                part.interiorResyncPending = pending;
            }
        }
    }
}
