using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Player soak (report: "after some time a player freezes for the others and shows up somewhere
    /// else"): both players spend DURATION seconds hopping, travelling far, boarding and riding a loco
    /// the host pushes, going home and idling. Each records where it really is and where it sees the
    /// other; the views must never stay more than MAX metres off for more than HOLD seconds. A
    /// dedicated host has no player: it only pushes the loco now and then and watches the client, and
    /// only its view of the client is judged.
    /// </summary>
    [Scenario("player-soak", "Both players hop, travel, board and ride for DURATION s: neither's view of the other stays more than MAX m off for more than HOLD s")]
    [Budget(900)]
    public sealed class PlayerSoak : Scenario<PlayerSoak.Options>
    {
        public sealed class Options
        {
            /// <summary>Seconds of play.</summary>
            public int Duration = 240;
            /// <summary>How far off a view may be (m)...</summary>
            public float Max = 3;
            /// <summary>...for at most this long (s).</summary>
            public float Hold = 5;
            /// <summary>The client's receive lag, ms.</summary>
            public int Lag;
            public int Seed = 1;
        }

        private const float HomeX = 7194f, HomeZ = 6873f;

        protected override async Task Run(Pair pair, Options o)
        {
            // Near the client (a dedicated host has no player); the framework deletes it after the run.
            string loco = await pair.Host.Run<string>(s => Cars.SpawnNearClient(s, "LocoDE2"));
            Played h, c;
            using (await pair.Client.Lag(o.Lag))
            {
                Report.Note($"both players act for {o.Duration}s (loco {loco})");
                var host = pair.Host.Start<Played>(s => InGame.Act(s, o.Duration, o.Seed, loco, HomeX, HomeZ), o.Duration + 120);
                var client = pair.Client.Start<Played>(s => InGame.Act(s, o.Duration, o.Seed + 100, loco, HomeX, HomeZ), o.Duration + 120);
                h = await host;
                c = await client;
            }
            Check("host's view of the client", c.Own, h.Seen, o);
            if (!pair.Dedicated) Check("client's view of the host", h.Own, c.Seen, o);
            Report.Note("client did: " + string.Join(", ", c.Log));
            Report.Note("host did:   " + string.Join(", ", h.Log));
        }

        // How far a peer's view of the other strays from where the other really was, allowing up to
        // 1.2 s of interpolation lag. Fails on a stretch off by more than MAX for HOLD or longer.
        private static void Check(string name, float[][] truth, float[][] view, Options o)
        {
            double worst = 0;
            float? runStart = null;
            double runPeak = 0;
            var bad = new List<string>();
            float hold = o.Hold * 1000;
            foreach (float[] s in view)
            {
                double best = double.PositiveInfinity;
                foreach (float[] t in truth)
                {
                    float dt = s[0] - t[0];
                    if (dt < -250 || dt > 1200) continue;
                    best = Math.Min(best, Math.Sqrt((s[1] - t[1]) * (s[1] - t[1]) + (s[3] - t[3]) * (s[3] - t[3])));
                }
                if (double.IsPositiveInfinity(best)) continue;
                worst = Math.Max(worst, best);
                if (best > o.Max)
                {
                    if (runStart == null) { runStart = s[0]; runPeak = 0; }
                    runPeak = Math.Max(runPeak, best);
                }
                else
                {
                    if (runStart != null && s[0] - runStart >= hold) bad.Add(Stretch(truth, runStart.Value, s[0] - runStart.Value, runPeak, false));
                    runStart = null;
                }
            }
            if (runStart != null && view.Length > 0 && view[view.Length - 1][0] - runStart >= hold)
                bad.Add(Stretch(truth, runStart.Value, view[view.Length - 1][0] - runStart.Value, runPeak, true));
            Report.Note($"{name}: {view.Length} samples, worst {worst:0.0} m");
            Expect.Empty(bad, $"{name}: stretches off by more than {o.Max} m for {o.Hold} s or longer");
        }

        private static string Stretch(float[][] truth, float from, float ms, double peak, bool open) =>
            $"from {(from - (truth.Length > 0 ? truth[0][0] : 0)) / 1000:0}s for {ms / 1000:0.0}s, peak {peak:0.0} m{(open ? " (still off at the end)" : "")}";

        public sealed class Played
        {
            /// <summary>This player's own position, [ms, x, y, z] four times a second (wall-clock ms,
            /// mod 10^7: both games run on one machine). Empty on a dedicated host.</summary>
            public float[][] Own;
            /// <summary>Where this game shows the other player's avatar, the same way.</summary>
            public float[][] Seen;
            public string[] Log;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Either peer, for <paramref name="seconds"/>: keeps doing things a player does (short hops,
            /// long trips (world streaming), boarding the test loco, riding it (the host pushes it), going
            /// home, standing still) while recording this player's own position and where it sees the
            /// other player's avatar. A dedicated host idles and pushes the loco.
            /// </summary>
            [Step]
            public static IEnumerator Act(Step<Played> step, float seconds, int seed, string locoId, float homeX, float homeZ)
            {
                var rnd = new System.Random(seed);
                var loco = Trains.Find(locoId);
                var home = new Vector2(homeX, homeZ);
                bool host = NetworkLifecycle.Instance.IsHost(), player = !NetworkLifecycle.Instance.IsDedicatedHost;
                var own = new List<float[]>(); var seen = new List<float[]>(); var log = new List<string>();
                bool running = true;
                long T() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                IEnumerator Sample()
                {
                    while (running)
                    {
                        if (player)
                        {
                            var me = Dev.Abs(PlayerManager.PlayerTransform.position);
                            own.Add(new float[] { T() % 10000000, me.x, me.y, me.z });
                        }
                        var other = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().FirstOrDefault();
                        if (other != null) { var p = Dev.Abs(other.transform.position); seen.Add(new float[] { T() % 10000000, p.x, p.y, p.z }); }
                        yield return new WaitForSecondsRealtime(0.25f);
                    }
                }
                CoroutineManager.Instance.StartCoroutine(Sample());
                IEnumerator Wait(float s) { for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < s;) yield return null; }
                float start = Time.realtimeSinceStartup;
                try
                {
                    while (Time.realtimeSinceStartup - start < seconds)
                    {
                        int a = player ? rnd.Next(host ? 6 : 5) : rnd.Next(2) == 0 ? 4 : 5;
                        float at = Time.realtimeSinceStartup - start;
                        switch (a)
                        {
                            case 0:
                                var me = Dev.Abs(PlayerManager.PlayerTransform.position);
                                log.Add($"{at:F0}s hop");
                                yield return LocalPlayer.TeleportAbs(me + new Vector3((float)rnd.NextDouble() * 12 - 6, 0, (float)rnd.NextDouble() * 12 - 6), 0.5f);
                                break;
                            case 1:
                                float ang = (float)(rnd.NextDouble() * Math.PI * 2), dist = 300f + (float)rnd.NextDouble() * 1200f;
                                log.Add($"{at:F0}s trip {dist:F0} m");
                                yield return LocalPlayer.TeleportToGround(home.x + Mathf.Cos(ang) * dist, home.y + Mathf.Sin(ang) * dist);
                                yield return Wait(3f);
                                break;
                            case 2:
                                log.Add($"{at:F0}s board");
                                yield return Trains.Board(loco);
                                yield return Wait(4f);
                                break;
                            case 3:
                                log.Add($"{at:F0}s home");
                                yield return LocalPlayer.TeleportToGround(home.x + (host ? 4f : 0f), home.y);
                                break;
                            case 4:
                                log.Add($"{at:F0}s idle");
                                yield return Wait(4f);
                                break;
                            case 5:
                                log.Add($"{at:F0}s push loco");
                                yield return Trains.Push(loco, 6f, 4f, 2f);
                                break;
                        }
                    }
                }
                finally { running = false; }
                step.Result = new Played { Own = own.ToArray(), Seen = seen.ToArray(), Log = log.ToArray() };
            }
        }
    }
}
