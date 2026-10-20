using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A derailed car off the track ballast, kilometres from the host's body. The server loads
    /// terrain around every remote player and every derailed car (ServerTerrainCoverage), as well as
    /// every Near tile (ServerWorldCoverage), so the host has terrain under the client's player, and
    /// a derailed car the host puts beside the track, 3 m above the terrain, falls onto it and comes
    /// to rest on the host, and the client's copy rests where the host's does. Before that, the host
    /// froze such a car in mid-air (no terrain there), and the client's copy fell to the client's
    /// terrain below it.
    /// </summary>
    [Scenario("far-derail-embankment", "On open line 4-8 km from the host's body the host has terrain under the client, and a derailed car moved off the track lands and rests on the host's terrain, the client's copy where the host's is")]
    [Budget(420)]
    public sealed class FarDerailEmbankment : Scenario<FarDerailEmbankment.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            FarDerail.Place line = await pair.Host.Run(() => InGame.OpenLine());
            Report.Note($"the client goes to open line at {line.X:0}, {line.Z:0}, {line.Distance} m from the host's body");
            await pair.Client.Run(() => Players.TeleportToGround(line.X, line.Z), 120);
            await Task.Delay(15000);   // the client's surroundings stream in, and the host's coverage follows the player there

            Under t = await pair.Host.Run(() => InGame.TerrainUnderClient());
            Report.Note($"host, under the client ({t.FromHostBody} m from the host's body): top {t.Top}, terrain {t.Terrain}, Far tile loaded {t.FarLoaded}");
            Require.That(t.FarLoaded, "the host has the Far scenery tile under the client's player loaded");
            Require.That(!string.IsNullOrEmpty(t.Terrain), "the host has terrain under the client's player");

            FarCar car = await pair.Host.Run<FarCar>(s => Cars.SpawnByClient(s, "TankChrome"), 150);
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { car.Id })));
            Report.Note($"spawned {car.Id} next to {car.Player}");
            Require.That(car.Ground, "ground under the car on the host", car.Ground ? $"after {car.Waited} s" : $"none after {car.Waited} s");

            Report.Note($"the host derails {car.Id} and lets it settle on the ballast");
            await pair.Host.Run(() => Cars.Derail(car.Id));
            await Task.Delay(8000);
            Moved m = await pair.Host.Run(() => InGame.OffTheTrack(car.Id));
            Require.That(m.Found, "terrain beside the track on the host, 15-80 m either side");
            Report.Note($"moved it {m.Distance} m off the track, 3 m above the host's terrain ({m.Terrain}); waiting 12 s");
            await Task.Delay(12000);   // it falls and settles
            CarRest h = await pair.Host.Run(() => Cars.Rest(car.Id));
            CarRest c = await pair.Client.Run(() => Cars.Rest(car.Id));
            float apart = Peers.Distance(h.Abs, c.Abs);
            Report.Note($"host: derailed {h.Derailed}, kinematic {h.Kinematic}, ground {h.GroundCollider}, speed {h.Speed}");
            Report.Note($"client: derailed {c.Derailed}, kinematic {c.Kinematic}, ground {c.GroundCollider}, {apart:0.00} m from the host's (host at {h.Abs}, client at {c.Abs})");
            Expect.That(h.Derailed, "still derailed on the host");
            Expect.That(!h.Kinematic, "the host let the car land on its terrain (not kinematic)");
            Expect.That(h.Ground, "ground under the car on the host");
            Expect.That(h.Speed < 0.5f, "the car came to rest on the host (under 0.5 m/s)", $"{h.Speed} m/s");
            Expect.That(apart < 1.5f, "the client's car where the host's is (under 1.5 m)", $"{apart:0.00} m");
        }

        public sealed class Under
        {
            public bool FarLoaded;
            public string Top, Terrain;
            public float FromHostBody;
        }

        public sealed class Moved
        {
            public bool Found;
            public float Distance, Y;
            public string Terrain;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: a point on open line, 4-8 km from the host's body and over 400 m from any
            /// station, so the ground beside the track is terrain rather than platforms and yards.</summary>
            [Step]
            public static FarDerail.Place OpenLine()
            {
                var body = PlayerManager.PlayerTransform.position;
                var stations = StationController.allStations.Select(s => s.transform.position).ToList();
                var rnd = new System.Random(7);
                var point = UnityEngine.Object.FindObjectsOfType<RailTrack>()
                    .Select(t => (Vector3)t.curve.GetPointAt(0.5f))
                    .Where(p => { var d = Vector3.Distance(p, body); return d > 4000 && d < 8000; })
                    .Where(p => stations.All(s => Vector3.Distance(s, p) > 400f))
                    .OrderBy(_ => rnd.Next())
                    .Cast<Vector3?>().FirstOrDefault() ?? throw new Exception("no open line 4-8 km from the host's body");
                var a = Dev.Abs(point);
                return new FarDerail.Place { Name = "open line", X = a.x, Z = a.z, Distance = Mathf.Round(Vector3.Distance(point, body)) };
            }

            /// <summary>Host: what's under the client's player (the server should have terrain there
            /// however far it is from the host's own body), and whether its Far scenery tile is loaded.</summary>
            [Step]
            public static Under TerrainUnderClient()
            {
                var player = Multiplayer.Server.Session.NetworkServer.Instance.GameplayPlayers.First(p => p != Multiplayer.Server.Session.NetworkServer.Instance.LocalPlayer);
                var hits = Physics.RaycastAll(player.WorldPosition + Vector3.up * 30f, Vector3.down, 80f, ~0, QueryTriggerInteraction.Ignore)
                    .Where(h => h.collider.attachedRigidbody == null).OrderBy(h => h.distance).ToList();
                var far = WorldStreamingInit.Instance.farScenesStreamer;
                return new Under
                {
                    FarLoaded = far != null && far.IsSceneLoaded(player.WorldPosition),
                    Top = hits.Count > 0 ? hits[0].collider.name : null,
                    Terrain = hits.Where(h => h.collider is TerrainCollider).Select(h => h.collider.name).FirstOrDefault(),
                    FromHostBody = Mathf.Round(Vector3.Distance(player.WorldPosition, PlayerManager.PlayerTransform.position))
                };
            }

            /// <summary>Host: moves a derailed car sideways off the track, 3 m above the nearest open
            /// spot (15-80 m either side) where the host has bare terrain under the whole car: every
            /// point of a grid over its footprint, plus a few metres around it, hits a terrain collider
            /// first, not ballast, a platform or a building.</summary>
            [Step]
            public static Moved OffTheTrack(string id)
            {
                var car = Dvmp.Lib.Trains.Find(id);
                RaycastHit? Top(Vector3 p)
                {
                    var hits = Physics.RaycastAll(p + Vector3.up * 30f, Vector3.down, 80f, ~0, QueryTriggerInteraction.Ignore)
                        .Where(h => h.collider.attachedRigidbody == null).OrderBy(h => h.distance).ToList();
                    return hits.Count > 0 ? hits[0] : (RaycastHit?)null;
                }
                var fwd = car.transform.forward; var right = car.transform.right;
                var footprint = new List<Vector3>();
                for (float a = -12f; a <= 12f; a += 4f)
                    for (float b = -5f; b <= 5f; b += 2.5f)
                        footprint.Add(car.transform.position + fwd * a + right * b);
                foreach (float d in new[] { 15f, 20f, 30f, 40f, 60f, 80f }.SelectMany(d => new[] { d, -d }))
                {
                    var side = right * d;
                    var tops = footprint.Select(p => Top(p + side)).ToList();
                    if (tops.Any(t => t == null || !(t.Value.collider is TerrainCollider)))
                        continue;
                    float lift = tops.Max(t => t.Value.point.y) + 3f - car.transform.position.y;
                    car.rb.velocity = Vector3.zero;
                    car.rb.angularVelocity = Vector3.zero;
                    car.transform.position += side + Vector3.up * lift;
                    Physics.SyncTransforms();
                    return new Moved { Found = true, Distance = Math.Abs(d), Y = Dev.Abs(car.transform.position).y, Terrain = tops[0].Value.collider.name };
                }
                return new Moved { Found = false };
            }
        }
    }
}
