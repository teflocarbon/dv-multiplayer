using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Arriving at a station: a player travels to a far station (whose jobs have not been generated
    /// yet) while both peers record frame times. Players reported everyone freezing for 1-2 s when
    /// someone reaches a station. Reports stalls per peer; fails if a frame took longer than MAX_MS.
    /// Not in the gate's list.
    /// </summary>
    [Scenario("station-load", "A player arrives at a far station while both peers record frame times: no frame takes longer than MAX_MS")]
    [Budget(300)]
    public sealed class StationLoad : Scenario<StationLoad.Options>
    {
        public sealed class Options
        {
            /// <summary>The n-th farthest station instead of the farthest.</summary>
            public int Skip;
            /// <summary>Who travels: client (the host stays and watches), host or both.</summary>
            public string Who = "client";
            /// <summary>The longest frame allowed, ms.</summary>
            public int MaxMs = 1000;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Peer traveller = Places.Player(pair, o.Who == "both" ? "host" : o.Who, "WHO");   // WHO=both needs the host's player too
            Far s = await pair.Host.Run(() => InGame.Pick(o.Skip));
            Report.Note($"{o.Who} travels to {s.Id}, {s.Km} km away; recording frames on both");
            var frames = pair.Both.Start<Frames>(f => InGame.Frames(f, 30f), 60);
            await Task.Delay(3000);
            if (o.Who == "both")
            {
                await pair.Client.Run(() => Players.TeleportToGround(s.X + 4f, s.Z), 60);
                await pair.Host.Run(() => Players.TeleportToGround(s.X, s.Z), 60);
            }
            else await traveller.Run(() => Players.TeleportToGround(s.X, s.Z), 60);
            foreach (var (peer, r) in await frames)
            {
                Report.Note($"{peer}: worst {r.WorstMs} ms, {r.StallsOver100Ms} stalls over 100 ms ({r.StalledSeconds} s), {string.Join(", ", r.Worst)}");
                Expect.AtMost(Math.Floor(r.WorstMs), o.MaxMs, $"{peer}: the longest frame (ms)");
            }
        }

        public sealed class Far
        {
            public string Id;
            public float X, Z, Km;
        }

        public sealed class Frames
        {
            public int Count, StallsOver100Ms;
            public float WorstMs, StalledSeconds;
            /// <summary>The five worst, as "ms@seconds in".</summary>
            public string[] Worst;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the station farthest from every player (or the <paramref name="skip"/>-th
            /// farthest), with its office position.</summary>
            [Step]
            public static Far Pick(int skip)
            {
                var players = new List<Vector3> { PlayerManager.PlayerTransform.position };
                players.AddRange(UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().Select(p => p.transform.position));
                var station = StationController.allStations
                    .OrderByDescending(s => players.Min(p => Vector3.Distance(p, s.transform.position)))
                    .Skip(skip).First();
                var at = Dev.Abs(station.transform.position);
                return new Far { Id = station.stationInfo.YardID, X = at.x, Z = at.z, Km = (float)Math.Round(players.Min(p => Vector3.Distance(p, station.transform.position)) / 1000f, 1) };
            }

            /// <summary>Records frame times for <paramref name="seconds"/>: the worst frames and the total
            /// time spent in frames over 100 ms (stalls a player would notice).</summary>
            [Step]
            public static IEnumerator Frames(Step<Frames> step, float seconds)
            {
                var frames = new List<(float at, float ms)>();
                float t0 = Time.realtimeSinceStartup, last = t0;
                while (Time.realtimeSinceStartup - t0 < seconds)
                {
                    yield return null;
                    float now = Time.realtimeSinceStartup;
                    frames.Add((now - t0, (now - last) * 1000f));
                    last = now;
                }
                step.Result = new Frames
                {
                    Count = frames.Count,
                    WorstMs = (float)Math.Round(frames.Max(f => f.ms)),
                    StallsOver100Ms = frames.Count(f => f.ms > 100f),
                    StalledSeconds = (float)Math.Round(frames.Where(f => f.ms > 100f).Sum(f => f.ms) / 1000f, 2),
                    Worst = frames.OrderByDescending(f => f.ms).Take(5).Select(f => $"{f.ms:F0}ms@{f.at:F1}s").ToArray()
                };
            }
        }
    }
}
