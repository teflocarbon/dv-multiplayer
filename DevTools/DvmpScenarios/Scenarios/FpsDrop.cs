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
    /// Frame-rate drop on a client riding a moving train: the client's frame rate is cut (default
    /// to 6 FPS) while the host pushes the train; the client's train must stay close to the host's
    /// during the drop and recover after it. A timing run: gate.sh runs it alone, every other pair down.
    /// </summary>
    [Scenario("fps-drop", "The client's frame rate drops to 6 FPS while riding a pushed train: its train stays within 3 m of the host's before, during and after")]
    [Budget(300)]
    public sealed class FpsDrop : Scenario<FpsDrop.Options>
    {
        public sealed class Options
        {
            /// <summary>The dropped frame rate.</summary>
            public int Fps = 6;
            /// <summary>m/s.</summary>
            public float Speed = 10;
            /// <summary>The worst along-track error allowed, m.</summary>
            public float Max = 3;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Loco loco = await pair.Host.Run<Loco>(s => Cars.SpawnLoco(s, "LocoDE2", false, false));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            pair.Cleanup(() => pair.Client.Run(() => Players.TeleportToGround(7190f, 6880f)));
            Report.Note($"== {loco.Id}; client boards");
            await pair.Client.Run(() => Players.Board(loco.Id));
            Report.Note($"== host pushes at {o.Speed} m/s; client drops to {o.Fps} FPS mid-run");
            var push = pair.Host.Start(() => InGame.Push(loco.Id, o.Speed, 26f), 120);
            await Task.Delay(6000);   // the train gets up to speed, as before
            Phase[] phases = await pair.Client.Run<Phase[]>(s => InGame.Measure(s, loco.Id, o.Fps, 7f), 90);
            await push;
            foreach (Phase p in phases)
            {
                Report.Note($"{p.Name,-6} {p.Fps,3} fps: worst {p.Worst} m, mean {p.Mean} m, game/real time {p.GameTimeRatio}, speed {p.Speed} m/s");
                Expect.AtMost(p.Worst, o.Max, $"{p.Name}: worst along-track error (m)");
            }
        }

        public sealed class Phase
        {
            public string Name;
            public int Fps;
            public float Worst, Mean, GameTimeRatio, Speed;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: pushes the loco's consist (ramp to <paramref name="speed"/> m/s over 3 s,
            /// hold, ramp down). No handbrakes after, unlike Cars.Push: as the bash scenario did.</summary>
            [Step]
            public static IEnumerator Push(string loco, float speed, float hold) => Trains.Push(Trains.Find(loco), speed, hold);

            /// <summary>Client, riding the loco: along-track error against the host's train (projected
            /// to now) and the client's speed, at normal frame rate, during a drop to
            /// <paramref name="fps"/>, and after it, <paramref name="seconds"/> each. The frame rate
            /// is the condition under test.</summary>
            [Step]
            public static IEnumerator Measure(Step<Phase[]> step, string loco, int fps, float seconds)
            {
                TrainCar car = Trains.Find(loco);
                var nb = car.Bogies[0].GetComponent<Multiplayer.Client.Trains.NetworkedBogie>()
                         ?? throw new Exception("no NetworkedBogie on " + car.ID);
                int normal = Application.targetFrameRate;
                var phases = new List<Phase>();
                try
                {
                    foreach (var (name, rate) in new[] { ("before", normal), ("drop", fps), ("after", normal) })
                    {
                        Application.targetFrameRate = rate;
                        float worst = 0f, sum = 0f;
                        int n = 0;
                        float t0 = Time.realtimeSinceStartup, g0 = Time.time;
                        while (Time.realtimeSinceStartup - t0 < seconds)
                        {
                            yield return null;
                            if (nb.TryGetAlongTrackError(car, out float e)) { worst = Mathf.Max(worst, Mathf.Abs(e)); sum += Mathf.Abs(e); n++; }
                        }
                        phases.Add(new Phase
                        {
                            Name = name, Fps = rate, Worst = (float)Math.Round(worst, 2), Mean = n > 0 ? (float)Math.Round(sum / n, 2) : -1,
                            GameTimeRatio = (float)Math.Round((Time.time - g0) / (Time.realtimeSinceStartup - t0), 2),
                            Speed = (float)Math.Round(Mathf.Abs(car.GetForwardSpeed()), 1)
                        });
                    }
                }
                finally
                {
                    Application.targetFrameRate = normal;
                }
                step.Result = phases.ToArray();
            }
        }
    }
}
