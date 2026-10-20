using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Sandbox cargo loader (Discord: "setting the cargo load with the sandbox controls is not
    /// synced"): a client fills a car with the comms radio's cargo loader, then empties it. Both
    /// peers must show the same cargo each time. Turns on the radio cheat mode on the host for the
    /// test (career saves hide the loader otherwise) and turns it back off.
    /// </summary>
    [Scenario("sandbox-cargo", "The client loads and empties a car with the comms radio's cargo loader: both peers show the same cargo each time")]
    [Budget(300)]
    public sealed class SandboxCargo : Scenario<SandboxCargo.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            bool was = await pair.Host.Run(() => InGame.CheatMode());
            // Near the client: a dedicated host has no player of its own. The framework deletes it after the run.
            string car = await pair.Host.Run<string>(s => Cars.SpawnNearClient(s, "BoxcarBrown"));
            pair.Cleanup(() => pair.Host.Run(() => InGame.SetCheatMode(was)));
            await pair.Host.Run(() => InGame.SetCheatMode(true));
            await Task.Delay(2000);

            foreach (bool load in new[] { true, false })
            {
                string loaded = await pair.Client.Run<string>(s => InGame.Load(s, car, load));
                await Task.Delay(2000);
                PeerResults<Cargo> c = await pair.Both.Run(() => InGame.State(car));
                Report.Note($"client {(load ? "load" : "unload")}s {car} ({loaded}): host {c.Host.Loaded} {c.Host.Amount}, client {c.Client.Loaded} {c.Client.Amount}");
                Expect.Equal(c.Host.Loaded, c.Client.Loaded, "the client's cargo matches the host's");
                if (load) Expect.That(c.Host.Loaded != "None", "the host's car was loaded", c.Host.Loaded);
                else Expect.Equal("None", c.Host.Loaded, "the host's car's cargo once emptied");
            }
        }

        public sealed class Cargo
        {
            public string Loaded;
            public float Amount;
        }

        // ---- runs inside the games
        public static class InGame
        {
            [Step]
            public static bool CheatMode() => DV.Globals.G.GameParams.CommsRadioCheatMode;

            [Step]
            public static void SetCheatMode(bool on) => DV.Globals.G.GameParams.CommsRadioCheatMode = on;

            /// <summary>Client: the comms radio's cargo loader on a car: fills it with the first cargo it
            /// can carry, or empties it, through the loader's own load/unload methods. Result: the cargo.</summary>
            [Step]
            public static IEnumerator Load(Step<string> step, string carId, bool load)
            {
                var car = Trains.Find(carId);
                var loader = Resources.FindObjectsOfTypeAll<DV.CommsRadioCargoLoader>().First(l => l.gameObject.scene.IsValid());
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(DV.CommsRadioCargoLoader).GetField("pointedCar", flags).SetValue(loader, car);
                DV.ThingTypes.CargoType_v2 cargo = load ? DV.Globals.G.Types.cargos.First(c => c.IsLoadableOnCarType(car.carLivery.parentType)) : null;
                typeof(DV.CommsRadioCargoLoader).GetField("selectedCargoType", flags).SetValue(loader, cargo);
                typeof(DV.CommsRadioCargoLoader).GetMethod(load ? "LoadPointedCar" : "UnloadPointedCar", flags).Invoke(loader, null);
                for (int i = 0; i < 120; i++) yield return null;
                step.Result = cargo?.id ?? "none";
            }

            [Step]
            public static Cargo State(string carId)
            {
                var car = Trains.Find(carId);
                return new Cargo { Loaded = car.LoadedCargo.ToString(), Amount = car.LoadedCargoAmount };
            }
        }
    }
}
