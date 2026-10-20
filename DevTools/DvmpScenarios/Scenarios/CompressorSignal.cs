using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A DE6 with its engine running and an empty main reservoir builds air (playtest 2: L-056 had no
    /// brakes). The compressor's on/off port is typed CONTROL, but DV's CompressorSimController
    /// writes it from the brake system, and only when the brake system's decision changes. Once the
    /// port held 0 while the brake system wanted the compressor on, nothing put it back: no air on
    /// any game. The host now keeps it matched to the brake system.
    /// 1. The host spawns a DE6, starts it and empties its main reservoir: the brake system wants
    ///    the compressor on. The client boards it.
    /// 2. The client's copy of the port goes to 0 (an unleased control change, as playtest 2 most
    ///    likely had it), then the reservoir is emptied again.
    /// 3. The host's own port goes to 0, then the reservoir is emptied again.
    /// After 2 and 3 the port is back to 1 and the main reservoir pressure rises on host and client.
    /// A dedicated host works too.
    /// </summary>
    [Scenario("compressor-signal", "A started loco's compressor signal, written to 0 on the client and then on the host, goes back to 1 and the main reservoir builds air on both")]
    [Budget(300)]
    public sealed class CompressorSignal : Scenario<CompressorSignal.Options>
    {
        public sealed class Options
        {
            public string Loco = "LocoDE6";
            /// <summary>Seconds of compressor time per step.</summary>
            public int Wait = 20;
            /// <summary>Bar the main reservoir must gain.</summary>
            public float Rise = 0.5f;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Loco loco = await pair.Host.Run<Loco>(s => Cars.SpawnLoco(s, o.Loco, true, false));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            pair.Cleanup(() => pair.Client.Run(() => Players.TeleportToGround(7190f, 6880f)));
            await pair.Host.Run(() => InGame.StartEmpty(loco.Id), 60);
            Report.Note($"== {o.Loco} {loco.Id} started, main reservoir emptied; the client boards it");
            await pair.Client.Run(() => Players.Board(loco.Id));
            await Task.Delay(3000);   // the cab settles after boarding, as before

            Report.Note("== the client's copy of the compressor signal goes to 0");
            await Step("client wrote 0", pair.Client);
            Report.Note("== the host's own compressor signal goes to 0");
            await Step("host wrote 0", pair.Host);

            async Task Step(string what, Peer who)
            {
                await who.Run(() => InGame.Write(loco.Id, 0f));
                await Task.Delay(2000);
                await pair.Host.Run(() => InGame.Empty(loco.Id));
                Signal h0 = await pair.Host.Run(() => InGame.Read(loco.Id));
                await Task.Delay(o.Wait * 1000);   // compressor time: the condition under test
                Signal h = await pair.Host.Run(() => InGame.Read(loco.Id));
                Signal c = await pair.Client.Run(() => InGame.Read(loco.Id));
                Report.Note($"{what}: brake system wants the compressor {(h.Wanted ? "on" : "off")}; port host {h.Port}, client {c.Port}; " +
                            $"main reservoir host {h0.Pressure} -> {h.Pressure} bar, client {c.Pressure} bar");
                Expect.That(h.Port == 1f, $"after '{what}' the host's compressor signal is 1", $"{h.Port}, with the brake system wanting it {(h.Wanted ? "on" : "off")}");
                Expect.That(h.Pressure - h0.Pressure >= o.Rise && c.Pressure - h0.Pressure >= o.Rise, $"after '{what}' the main reservoir built air on both (needs +{o.Rise} bar)",
                    $"host {h0.Pressure} -> {h.Pressure}, client {c.Pressure} bar");
            }
        }

        public sealed class Signal
        {
            /// <summary>The compressor activation signal port.</summary>
            public float Port;
            /// <summary>What the brake system wants it to be.</summary>
            public bool Wanted;
            /// <summary>Main reservoir pressure, bar.</summary>
            public float Pressure;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: starts the loco and empties its main reservoir.</summary>
            [Step]
            public static IEnumerator StartEmpty(string loco)
            {
                TrainCar car = Trains.Find(loco);
                yield return Trains.StartLoco(car);
                car.brakeSystem.SetMainReservoirPressure(1f);
                yield return new WaitForSeconds(2);
            }

            /// <summary>Host: empties the loco's main reservoir again.</summary>
            [Step]
            public static void Empty(string loco) => Trains.Find(loco).brakeSystem.SetMainReservoirPressure(1f);

            /// <summary>Either peer: the compressor activation signal port, what the brake system wants it
            /// to be, and the main reservoir pressure.</summary>
            [Step]
            public static Signal Read(string loco)
            {
                TrainCar car = Trains.Find(loco);
                return new Signal
                {
                    Port = Port(car).Value,
                    Wanted = car.brakeSystem.compressorActivationSignal,
                    Pressure = (float)Math.Round(car.brakeSystem.mainReservoirPressure, 2)
                };
            }

            /// <summary>Either peer: writes the port, as anything that isn't the brake system would (an
            /// echo of a control change).</summary>
            [Step]
            public static void Write(string loco, float value) => Port(Trains.Find(loco)).ExternalValueUpdate(value);

            private static LocoSim.Implementations.Port Port(TrainCar car)
            {
                var flow = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>().simulationFlow;
                var compressor = car.GetComponentsInChildren<DV.Simulation.Brake.CompressorSimController>(true).FirstOrDefault()
                    ?? throw new Exception(car.ID + " has no compressor");
                return flow.TryGetPort(compressor.activationSignalExtInPortId, out var port) ? port
                    : throw new Exception("no port " + compressor.activationSignalExtInPortId);
            }
        }
    }
}
