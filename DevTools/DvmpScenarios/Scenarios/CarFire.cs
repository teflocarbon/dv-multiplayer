using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Radio damage (#74) and car fires from a client. The client's comms radio damages a gasoline
    /// tank car until it leaks, and lights the leak with a lighter's igniter: the host must take the
    /// damage, burn and explode the car, and both peers must agree throughout. Then the host lights a
    /// diesel tank of its own, and the client must show it burning. Turns the radio's cheat mode on in
    /// both games for the run.
    /// </summary>
    [Scenario("car-fire", "The client's radio damages a gasoline tank until it leaks and lights it: both peers see it burn and explode; a diesel tank the host lights burns on the client")]
    [Budget(300)]
    public sealed class CarFire : Scenario<CarFire.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            await pair.Both.Run(() => InGame.CheatMode());   // the result is only so both games run it at once

            Report.Note("== the client's radio damages a gasoline tank");
            Loco tank = await Tank("Gasoline");
            for (int i = 0; i < 2; i++)
            {
                await pair.Client.Run<Damaged>(s => InGame.Radio(s, tank.Id, 4), 60);
                await Task.Delay(2000);
            }
            await ExpectBoth(tank.Id, "after two hits", f => f.Leaking, "leaking", f => $"leaking {f.Leaking}, health {f.Health}");

            Report.Note("== the client lights the leak");
            await pair.Client.Run<bool>(s => InGame.Light(s, tank.Id));
            await Task.Delay(2000);
            await ExpectBoth(tank.Id, "burning", f => f.Ignited, "ignited", f => $"ignited {f.Ignited}, about to explode {f.AboutToExplode}");
            // Up to a minute for the fire to explode it.
            for (int i = 0; i < 12; i++)
            {
                PeerResults<Fire> f = await pair.Both.Run(() => InGame.State(tank.Id));
                if (f.Host.Exploded && f.Client.Exploded) break;
                await Task.Delay(5000);
            }
            await ExpectBoth(tank.Id, "then", f => f.Exploded, "exploded", f => $"exploded {f.Exploded}");

            Report.Note("== the host lights a diesel tank of its own");
            Loco diesel = await Tank("Diesel");
            for (int i = 0; i < 2; i++)
            {
                await pair.Host.Run<Damaged>(s => InGame.Radio(s, diesel.Id, 4), 60);
                await Task.Delay(2000);
            }
            await pair.Host.Run<bool>(s => InGame.Light(s, diesel.Id));
            await Task.Delay(2000);
            await ExpectBoth(diesel.Id, "burning", f => f.Ignited, "ignited", f => $"ignited {f.Ignited}");

            async Task<Loco> Tank(string cargo)
            {
                Loco t = await pair.Host.Run<Loco>(s => InGame.SpawnTank(s, cargo));
                pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, t.Cars)));
                return t;
            }

            // Both peers have the wanted state and agree on every field shown.
            async Task ExpectBoth(string car, string what, Func<Fire, bool> wanted, string name, Func<Fire, string> show)
            {
                PeerResults<Fire> f = await pair.Both.Run(() => InGame.State(car));
                Expect.That(wanted(f.Host) && wanted(f.Client) && show(f.Host) == show(f.Client), $"{what}: {name} on both, the same on both",
                    $"host {show(f.Host)}; client {show(f.Client)}" + (f.Host.Reaction != null ? "; host: " + f.Host.Reaction : "") + (f.Client.Reaction != null ? "; client: " + f.Client.Reaction : ""));
            }
        }

        public sealed class Fire
        {
            /// <summary>Set when the car has no set-up cargo reaction on this game.</summary>
            public string Reaction;
            public bool Leaking, Ignited, AboutToExplode, Exploded;
            public float Health;
        }

        public sealed class Damaged
        {
            /// <summary>This game has no comms radio damage mode: nothing was done.</summary>
            public bool NoRadio;
            public float Before, After, Max;
            public bool Derailed;
        }

        // ---- runs inside the games
        public static class InGame
        {
            [Step]
            public static bool CheatMode() => DV.Globals.G.GameParams.CommsRadioCheatMode = true;

            /// <summary>Host: a TankChrome near the host's player, handbrake on, loaded with a full load
            /// of <paramref name="cargo"/>.</summary>
            [Step]
            public static IEnumerator SpawnTank(Step<Loco> step, string cargo)
            {
                yield return Cars.SpawnLoco(step, "TankChrome", true, false);
                TrainCar c = Trains.Find(step.Result.Id);
                c.logicCar.LoadCargo(c.cargoCapacity, DV.Globals.G.Types.cargos.First(x => x.id == cargo).v1);
                for (int i = 0; i < 180; i++) yield return null;
            }

            /// <summary>Either peer: the car's cargo fire state on this game, from its first set-up
            /// cargo reaction.</summary>
            [Step]
            public static Fire State(string car)
            {
                TrainCar c = Trains.Find(car);
                var r = Reaction(c);
                if (r == null) return new Fire { Reaction = "none initialised", Exploded = c.isExploded };
                return new Fire
                {
                    Leaking = r.cargoLeak != null && r.cargoLeak.IsLeaking(), Ignited = r.isIgnited, AboutToExplode = r.aboutToExplode,
                    Exploded = c.isExploded, Health = (float)Math.Round(c.CarDamage.currentHealth)
                };
            }

            /// <summary>Either peer: a lighter at the car's leak: the game's own Igniter, as on a
            /// lighter or flare item. Result: whether it's ignited on this game.</summary>
            [Step]
            public static IEnumerator Light(Step<bool> step, string car)
            {
                var r = Reaction(Trains.Find(car)) ?? throw new Exception("no set-up cargo reaction on " + car);
                var go = new GameObject("dvmp test lighter");
                go.transform.position = r.cargoLeak.Position();
                var igniter = go.AddComponent<DV.Hazmat.Igniter>();
                igniter.ignitionStrength = 100f;
                igniter.objectsRadius = 3f;
                igniter.IgniteNow();
                yield return new WaitForSecondsRealtime(0.5f);
                UnityEngine.Object.Destroy(go);
                step.Result = r.isIgnited;
            }

            /// <summary>Either peer: this game's comms radio damage mode used on a car at a damage
            /// <paramref name="level"/> (1-10), as a radio click does. Result: the car's health and
            /// derailed state here, before and after.</summary>
            [Step]
            public static IEnumerator Radio(Step<Damaged> step, string car, int level)
            {
                TrainCar c = Trains.Find(car);
                var radio = Resources.FindObjectsOfTypeAll<CommsRadioDamage>().FirstOrDefault(r => r.gameObject.scene.IsValid());
                if (radio == null)
                {
                    step.Note("no comms radio damage mode in this game");
                    step.Result = new Damaged { NoRadio = true };
                    yield break;
                }
                float before = c.CarDamage.currentHealth;
                radio.opMode = CommsRadioDamage.OperationMode.Damage;
                radio.damageLevel = level;
                radio.pointedCar = c;
                radio.OnUse();
                yield return new WaitForSecondsRealtime(3f);
                step.Result = new Damaged
                {
                    Before = (float)Math.Round(before), After = (float)Math.Round(c.CarDamage.currentHealth),
                    Max = (float)Math.Round(c.CarDamage.maxHealth), Derailed = c.derailed
                };
            }

            private static CargoReactionBase Reaction(TrainCar c) =>
                (c.interior != null ? c.interior.GetComponentsInChildren<CargoReactionBase>(true) : c.GetComponentsInChildren<CargoReactionBase>(true)).FirstOrDefault(x => x.initialized);
        }
    }
}
