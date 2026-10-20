using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Explosions (Discord: "explosions aren't synced"; cars derailed by an explosion on one screen
    /// only). For a loco's fuel explosion and a gasoline tank car's cargo explosion: the client trying
    /// to set one off itself must do nothing anywhere; the host setting it off must leave both peers
    /// with the same burnt-out car, the same derailments and the same positions.
    /// </summary>
    [Scenario("explosion", "A client can't set off a loco's or tank car's explosion; the host's leaves both peers with the same burnt car, derailments and positions")]
    [Budget(600)]
    public sealed class Explosion : Scenario<Explosion.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            Report.Note("== loco fuel explosion (a wagon beside it)");
            Coupling c = await pair.Host.Run<Coupling>(s => Couplings.SpawnCoupled(s, "BoxcarBrown", "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, c.Cars)));
            await Task.Delay(3000);
            await Explode(pair, c.Loco, cargo: false, c.Cars);

            Report.Note("== gasoline tank explosion");
            string tank = await pair.Host.Run<string>(s => InGame.SpawnGasolineTank(s));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { tank })));
            await Explode(pair, tank, cargo: true, new[] { tank });
        }

        private static async Task Explode(Pair pair, string car, bool cargo, string[] cars)
        {
            string kind = cargo ? "cargo explosion" : "fuel explosion";
            await pair.Client.Run<bool>(s => InGame.Boom(s, car, cargo));
            await Task.Delay(2000);
            bool host = (await pair.Host.Run(() => InGame.State(new[] { car })))[0].Exploded;
            bool client = (await pair.Client.Run(() => InGame.State(new[] { car })))[0].Exploded;
            Require.That(!host && !client, $"a client setting off a {kind} did nothing", $"exploded on the host {host}, on the client {client}");
            await pair.Host.Run<bool>(s => InGame.Boom(s, car, cargo));
            await Task.Delay(8000);
            Require.That((await pair.Client.Run(() => InGame.State(new[] { car })))[0].Exploded, $"the client saw the host's {kind}");

            // A wreck can still be settling on the client: three looks 5 s apart, once the host's cars are still.
            await pair.Host.Run(() => InGame.Settle(cars), 60);
            List<string> bad = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                Burnt[] h = await pair.Host.Run(() => InGame.State(cars));
                Burnt[] k = await pair.Client.Run(() => InGame.State(cars));
                bad = new List<string>();
                var line = new List<string>();
                for (int i = 0; i < h.Length; i++)
                {
                    float d = Peers.Horizontal(h[i].Abs, k[i].Abs);
                    line.Add($"{h[i].Id}: exploded {h[i].Exploded}/{k[i].Exploded}, derailed {h[i].Derailed}/{k[i].Derailed}, apart {d:0.00} m");
                    if (h[i].Exploded != k[i].Exploded || h[i].Derailed != k[i].Derailed || d > 1) bad.Add(h[i].Id);
                }
                Report.Note(string.Join(" | ", line) + " (host/client)");
                if (bad.Count == 0) break;
                if (attempt < 3) await Task.Delay(5000);
            }
            Expect.Empty(bad, $"{kind}: cars the peers disagree on (exploded, derailed, over 1 m apart)");
        }

        public sealed class Burnt
        {
            public string Id;
            public bool Exploded, Derailed;
            public Abs Abs;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: a gasoline-loaded tank car near the host's player (a dedicated host's
            /// parked body), handbrake on.</summary>
            [Step]
            public static IEnumerator SpawnGasolineTank(Step<string> step)
            {
                var c = Trains.SpawnNearPlayer("TankChrome");
                Cars.Record(new[] { c });
                c.brakeSystem.SetHandbrakePosition(1f);
                for (int i = 0; i < 90; i++) yield return null;
                c.logicCar.LoadCargo(c.cargoCapacity, DV.Globals.G.Types.cargos.First(x => x.id == "Gasoline").v1);
                for (int i = 0; i < 180; i++) yield return null;
                step.Result = c.ID;
            }

            /// <summary>Either peer: sets off the car's fuel explosion (ResourceExplosionBase) or, with
            /// <paramref name="cargo"/>, its cargo explosion. Result: whether it's exploded here.</summary>
            [Step]
            public static IEnumerator Boom(Step<bool> step, string id, bool cargo)
            {
                var car = Trains.Find(id);
                if (cargo) (car.interior != null ? car.interior.GetComponentsInChildren<CargoReactionBase>(true) : car.GetComponentsInChildren<CargoReactionBase>(true)).First().ExplodeCargo();
                else car.GetComponentsInChildren<ResourceExplosionBase>(true).First().ExplodeResource();
                for (int i = 0; i < 30; i++) yield return null;
                step.Result = car.isExploded;
            }

            [Step]
            public static Burnt[] State(string[] ids) => ids.Select(Trains.Find)
                .Select(c => new Burnt { Id = c.ID, Exploded = c.isExploded, Derailed = c.derailed, Abs = Positions.Of(c.transform.position) }).ToArray();

            /// <summary>Host: up to 40 s until the cars have been still for 2 s (a tumbling wreck
            /// passes through zero speed), and a second more.</summary>
            [Step]
            public static IEnumerator Settle(string[] ids)
            {
                var cs = ids.Select(Trains.Find).ToList();
                float still = 0f;
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 40f && still < 2f;)
                {
                    yield return null;
                    still = cs.Any(c => c.rb.velocity.magnitude > 0.05f) ? 0f : still + Time.unscaledDeltaTime;
                }
                yield return new WaitForSeconds(1);
            }
        }
    }
}
