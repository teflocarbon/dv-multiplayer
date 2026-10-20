using System;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A derailment next to the client, kilometres from the host's own body (its parked body on a
    /// dedicated host, its player on a player host). DV only loads terrain and scenery around one
    /// point and freezes a derailed car (kinematic) where its whole cell isn't loaded, so it used to
    /// hang in mid-air on the host. The server keeps every Near tile (the track ballast) loaded and
    /// lets a derailed car move wherever there's ground under it (ServerWorldCoverage), and loads
    /// terrain and Far tiles around derailed cars and remote players (ServerTerrainCoverage,
    /// ServerSceneryCoverage). The car must come to rest on the ground on the host with its whole
    /// cell loaded there, and the client's copy where the host has it.
    /// </summary>
    [Scenario("far-derail", "A tank car derailed next to the client, 4-8 km from the host's body, comes to rest on the ground on the host with its whole cell loaded, and the client's copy where the host has it")]
    [Budget(420)]
    public sealed class FarDerail : Scenario<FarDerail.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            Place station = await pair.Host.Run(() => InGame.FarStation());
            Report.Note($"the client goes to {station.Name}, {station.Distance} m from the host's body");
            await pair.Client.Run(() => Players.TeleportToGround(station.X, station.Z), 120);
            await Task.Delay(15000);   // the client's surroundings stream in, and the host's coverage follows the player there

            FarCar car = await pair.Host.Run<FarCar>(s => Cars.SpawnByClient(s, "TankChrome"), 150);
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { car.Id })));
            Report.Note($"spawned {car.Id} next to {car.Player}; ground under it on the host after {car.Waited} s; dedicated host: {car.Dedicated}");
            Require.That(car.Ground, "ground under the car on the host", $"none after {car.Waited} s (server world coverage: {car.Coverage})");

            Report.Note($"the host derails {car.Id}");
            await pair.Host.Run(() => Cars.Derail(car.Id));
            await Task.Delay(12000);   // it falls and settles
            CarRest h = await pair.Host.Run(() => Cars.Rest(car.Id));
            CarRest c = await pair.Client.Run(() => Cars.Rest(car.Id));
            float apart = Peers.Distance(h.Abs, c.Abs);
            Report.Note($"host: derailed {h.Derailed}, kinematic {h.Kinematic}, whole cell loaded {h.Loaded}, ground {h.GroundCollider}, speed {h.Speed}, {h.FromHostBody} m from its body");
            Report.Note($"client: derailed {c.Derailed}, kinematic {c.Kinematic}, {apart:0.00} m from the host's");
            Expect.That(h.Derailed, "the test derail happened on the host");
            Expect.That(!h.Kinematic, "the host didn't freeze the derailed car (kinematic)");
            Expect.That(h.Loaded, "the host has the car's whole cell loaded (Near and Far tiles, terrain)");
            Expect.That(h.Ground, "ground under the car on the host");
            Expect.That(h.Speed < 0.5f, "the car came to rest on the host (under 0.5 m/s)", $"{h.Speed} m/s");
            Expect.That(c.Derailed, "derailed on the client");
            Expect.That(apart < 1.5f, "the client's car where the host's is (under 1.5 m)", $"{apart:0.00} m");
        }

        public sealed class Place
        {
            public string Name;
            public float X, Z, Distance;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the nearest station office 4-8 km from the host's body.</summary>
            [Step]
            public static Place FarStation()
            {
                var body = PlayerManager.PlayerTransform.position;
                var st = StationController.allStations.Select(x => new { x, d = Vector3.Distance(x.transform.position, body) })
                    .Where(x => x.d > 4000 && x.d < 8000).OrderBy(x => x.d).FirstOrDefault()
                    ?? throw new Exception("no station 4-8 km from the host's body");
                var a = Dev.Abs(st.x.transform.position);
                return new Place { Name = st.x.stationInfo.YardID, X = a.x, Z = a.z, Distance = Mathf.Round(st.d) };
            }
        }
    }
}
