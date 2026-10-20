using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Pooled car reuse (#69): the host deletes cars and spawns cars of the same livery in the same
    /// frame, so DV's car pool hands the deleted car's object straight to the new car (on the client
    /// too, when the delete and spawn arrive in one packet batch). The new car used to keep the
    /// deleted car's doomed NetworkedTrainCar: the host announced it with the old net ID, the client
    /// rejected that ID as taken, and both ended up with an unnetworked car that blocked every
    /// topology snapshot on the client from then on. Pass: every car is networked on both peers, each
    /// new car has a fresh net ID that matches on the client, and no empty trainset is left behind.
    /// </summary>
    [Scenario("car-pool-reuse", "The host deletes cars and spawns the same livery in one frame: every car stays networked on both peers, with a fresh net ID that matches on the client, and no empty trainset is left (#69)")]
    [Budget(300)]
    public sealed class CarPoolReuse : Scenario<CarPoolReuse.Options>
    {
        public sealed class Options
        {
            /// <summary>Cars in the second round (the first has one).</summary>
            public int N = 3;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            PeerResults<string[]> before = await pair.Both.Run(() => InGame.Unnetworked());
            int emptyBefore = await pair.Host.Run(() => InGame.EmptyTrainsets());
            if (before.Host.Length + before.Client.Length > 0)
                Report.Note($"unnetworked before the test: host [{string.Join(",", before.Host)}] client [{string.Join(",", before.Client)}]");

            foreach (int n in new[] { 1, o.N })
            {
                Respawned r = await pair.Host.Run<Respawned>(s => InGame.Respawn(s, n));
                pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, r.Cars)));
                Report.Note($"the host deleted {n} boxcar(s) and spawned {n} in the same frame on {r.Track}: the pool reused {r.Reused} of {n} objects; " +
                            $"old net IDs {string.Join(",", r.OldIds)}; new {string.Join(",", r.Cars.Select((c, i) => c + ":" + r.NetIds[i]))}");
                await Task.Delay(2000);
                string[] client = await pair.Client.Run(() => InGame.NetIds(r.Cars));
                Report.Note("client " + string.Join(",", r.Cars.Select((c, i) => c + ":" + client[i])));
                var bad = new List<string>();
                for (int i = 0; i < r.Cars.Length; i++)
                {
                    string id = r.Cars[i], host = r.NetIds[i].ToString();
                    if (host == "0") bad.Add(id + " has no net ID on the host");
                    else if (r.OldIds.Contains(r.NetIds[i])) bad.Add($"{id} kept the deleted car's net ID {host}");
                    if (client[i] != host) bad.Add($"{id} is {(client[i] == "0" ? "unnetworked" : client[i])} on the client, {host} on the host");
                }
                Expect.Empty(bad, $"{n} car(s): new cars networked on both peers, with a fresh net ID the client agrees on");
                await pair.Host.Run<string[]>(s => Cars.Delete(s, r.Cars));   // frees the track for the next round
            }

            PeerResults<string[]> after = await pair.Both.Run(() => InGame.Unnetworked());
            int emptyAfter = await pair.Host.Run(() => InGame.EmptyTrainsets());
            Report.Note($"after: unnetworked host [{string.Join(",", after.Host)}] client [{string.Join(",", after.Client)}]; empty trainsets {emptyBefore} -> {emptyAfter}");
            foreach (var (peer, now) in after)
                Expect.Equal(string.Join(",", peer == "host" ? before.Host : before.Client), string.Join(",", now), $"{peer}: unnetworked cars (as before the test)");
            Expect.Equal(emptyBefore, emptyAfter, "empty trainsets on the host (as before the test; one makes the host refuse joins)");
        }

        public sealed class Respawned
        {
            public string Track;
            public string[] Cars;
            /// <summary>The new cars' net IDs, in Cars' order.</summary>
            public ushort[] NetIds;
            public ushort[] OldIds;
            public int Reused, EmptyTrainsets;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Every car without a NetworkedTrainCar in this game.</summary>
            [Step]
            public static string[] Unnetworked() =>
                CarSpawner.Instance.AllCars.Where(c => c != null && c.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>() == null).Select(c => c.ID).ToArray();

            [Step]
            public static int EmptyTrainsets() => Trainset.allSets.Count(s => s.cars.Count == 0);

            /// <summary>Each car's net ID in this game: 0 unnetworked, "missing" if it isn't here.</summary>
            [Step]
            public static string[] NetIds(string[] ids) => ids.Select(id =>
            {
                var c = CarSpawner.Instance.AllCars.FirstOrDefault(x => x != null && x.ID == id);
                return c == null ? "missing" : (c.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>()?.NetId ?? 0).ToString();
            }).ToArray();

            /// <summary>
            /// Host: spawns <paramref name="n"/> boxcars as one consist on a track with no other cars
            /// on it, waits for the client to have them, then deletes them and spawns <paramref name="n"/>
            /// new boxcars on the same track in the same frame, so DV's car pool hands the deleted
            /// objects to the new cars. Cars must never be spawned inside each other: overlapping cars
            /// couple into loops that DV's trainset code can't split when they break apart, which leaves
            /// an empty trainset behind.
            /// </summary>
            [Step]
            public static IEnumerator Respawn(Step<Respawned> step, int n)
            {
                var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First();
                var liveries = Enumerable.Repeat(Cars.Livery("BoxcarBrown"), n).ToList();
                var flips = liveries.Select(_ => false).ToList();
                ushort NetId(TrainCar c) => c.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>()?.NetId ?? 0;

                RailTrack track = null;
                foreach (float d in new[] { 30f, 60f, 90f, 120f, 160f, 200f })
                foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
                {
                    if (track != null) break;
                    var t = CarSpawner.GetTrackClosestTo(client.transform.position + dir * d, 60f, out int _);
                    if (t == null || t.GetKinkedPointSet().span < 80f) continue;
                    if (CarSpawner.Instance.AllCars.Any(c => c != null && c.Bogies.Any(b => b.track == t))) continue;
                    track = t;
                }
                if (track == null) throw new Exception("no empty track near the client");

                var old = CarSpawner.Instance.SpawnCarTypesOnTrack(liveries, flips, track, true, false) ?? throw new Exception("couldn't spawn on " + track.name);
                Cars.Record(old);
                for (int i = 0; i < 120; i++) yield return null;
                var oldIds = old.Select(NetId).ToArray();
                var oldObjects = new HashSet<TrainCar>(old);
                foreach (var c in old) CarSpawner.Instance.DeleteCar(c);
                var fresh = CarSpawner.Instance.SpawnCarTypesOnTrack(liveries, flips, track, true, false) ?? throw new Exception("couldn't respawn on " + track.name);
                Cars.Record(fresh);
                for (int i = 0; i < 120; i++) yield return null;
                step.Result = new Respawned
                {
                    Track = track.name,
                    Cars = fresh.Select(c => c.ID).ToArray(),
                    NetIds = fresh.Select(NetId).ToArray(),
                    OldIds = oldIds,
                    Reused = fresh.Count(c => oldObjects.Contains(c)),
                    EmptyTrainsets = Trainset.allSets.Count(s => s.cars.Count == 0)
                };
            }
        }
    }
}
