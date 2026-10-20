using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A station's loco spawner puts locos on its spawn track when a player comes near
    /// (Server/Trains/StationLocoSpawnerPatch: the server decides, from every player's position on
    /// the server), once per visit, and the client has them too. The host picks a spawner nobody is
    /// near, preferring one whose spawn track has no loco (else it deletes the cars on it), a player
    /// walks to its track, and within 20 s the host's spawn track has a loco, the client has the same
    /// cars, and 5 s later no second set has come. The spawned cars are deleted afterwards. On a
    /// dedicated host the client walks there (the server reads its position); `WHO=host` (a player
    /// host): the host's own player, whose position the server reads from its game with
    /// `--local-session` and without.
    /// </summary>
    [Scenario("station-loco-spawn", "A station's loco spawner spawns once when a player comes near, and the client has the locos")]
    [Budget(180)]
    public sealed class StationLocoSpawn : Scenario<StationLocoSpawn.Options>
    {
        public sealed class Options
        {
            /// <summary>client or host: who walks to the spawn track; host needs a player host.</summary>
            public string Who = "client";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            bool host = o.Who == "host";
            Require.That(host || o.Who == "client", "WHO is client or host", o.Who);
            if (host && pair.Dedicated)
            {
                Report.Note("a dedicated host has no player to walk there: skipped");
                return;
            }
            Peer who = host ? pair.Host : pair.Client;

            Spawner s = await pair.Host.Run(() => InGame.Pick());
            Report.Note($"spawner on {s.Track} at {s.At}, range {s.Range:0} m; on its track: {(s.Cars.Length == 0 ? "nothing" : string.Join(", ", s.Cars))}");
            if (s.Cars.Length > 0)
                Expect.Empty(await pair.Host.Run<string[]>(st => Cars.Delete(st, s.Cars)), "cars left on the spawn track after clearing it");

            await who.Run(() => Players.TeleportToGround(s.At.X + 6f, s.At.Z + 6f), 90);
            string[] locos = await Until.Value(() => pair.Host.Run(() => InGame.OnTrack(s.Track)), c => c.Length > 0, 20,
                "cars on the host's spawn track once the player is near");
            pair.Cleanup(async () => { await pair.Host.Run<string[]>(st => Cars.Delete(st, locos)); });
            Report.Note("spawned: " + string.Join(", ", locos));
            await Until.Value(() => pair.Client.Run(() => Cars.Existing(locos)), c => c.Length == locos.Length, 20,
                "the spawned cars on the client");
            await Task.Delay(5000);   // a second spawn for the same visit would come within a few checks (1 s apart)
            Expect.Equal(locos.Length, (await pair.Host.Run(() => InGame.OnTrack(s.Track))).Length,
                "cars on the host's spawn track 5 s later (spawned once)");
        }

        public sealed class Spawner
        {
            public string Track;
            public Abs At;
            public float Range;
            public string[] Cars;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: a loco spawner nobody is near, one with no loco on its spawn track if
            /// there is one: its track, where its spawn track's middle is, its range, the cars on it.</summary>
            [Step]
            public static Spawner Pick()
            {
                StationLocoSpawner pick = Object.FindObjectsOfType<StationLocoSpawner>()
                    .Where(s => s != null && s.locoSpawnTrack != null && s.spawnTrackMiddleAnchor != null && !s.playerEnteredLocoSpawnRange)
                    .OrderBy(s => CarsOn(s.locoSpawnTrack).Any(c => DV.ThingTypes.CarTypes.IsLocomotive(c.logicCar.carType)) ? 1 : 0)
                    .FirstOrDefault() ?? throw new System.InvalidOperationException("no loco spawner nobody is near");
                return new Spawner
                {
                    Track = pick.locoSpawnTrack.name,
                    At = Positions.Of(pick.spawnTrackMiddleAnchor.transform.position),
                    Range = Mathf.Sqrt(pick.spawnLocoPlayerSqrDistanceFromTrack),
                    Cars = CarsOn(pick.locoSpawnTrack).Select(c => c.ID).ToArray(),
                };
            }

            /// <summary>Either peer: the cars fully on the named spawn track, by ID.</summary>
            [Step]
            public static string[] OnTrack(string track)
            {
                StationLocoSpawner spawner = Object.FindObjectsOfType<StationLocoSpawner>().First(s => s.locoSpawnTrack != null && s.locoSpawnTrack.name == track);
                return CarsOn(spawner.locoSpawnTrack).Select(c => c.ID).ToArray();
            }

            private static TrainCar[] CarsOn(RailTrack track)
            {
                var onTrack = track.LogicTrack().GetCarsFullyOnTrack();
                return CarSpawner.Instance.AllCars.Where(c => c != null && onTrack.Contains(c.logicCar)).ToArray();
            }
        }
    }
}
