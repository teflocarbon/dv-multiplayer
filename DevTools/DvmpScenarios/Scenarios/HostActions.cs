using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dvmp.Agent;
using Multiplayer.Shared.World;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The host's own world takes other players' actions, and the host's actions reach the others,
    /// each applied once (docs/design/host-local-session.md, stage 2: the server applies junctions,
    /// turntables and time skips itself, not the host's own client). One player throws the junction
    /// nearest it, turns the nearest turntable half round (both players go to it), fast-travels and
    /// sleeps; after each, both games must agree: the same branch (switched exactly once on each
    /// game), the same turntable rotation, each clock skipped forward once, and the same shared sleep
    /// cooldown.
    /// </summary>
    [Scenario("host-actions", "A player throws a junction, turns a turntable, fast-travels and sleeps: each reaches the other game once, and both agree")]
    [Budget(600)]
    public sealed class HostActions : Scenario<HostActions.Options>
    {
        public sealed class Options
        {
            /// <summary>Who acts: client or host.</summary>
            public string Who = "client";
            /// <summary>all, or one of junction, turntable, fast-travel, sleep.</summary>
            public string Mode = "all";
            /// <summary>The time skip, game seconds.</summary>
            public int Skip = 3600;
        }

        // What the host logs when it gets a relayed junction, turntable or time skip it shouldn't.
        private static readonly Regex RelayWarning = new("Ignored (CommonChangeJunctionPacket|CommonRotateTurntablePacket|ClientboundTimeAdvancePacket) sent to the host's own client|which doesn't exist here");

        protected override async Task Run(Pair pair, Options o)
        {
            Peer who = Places.Player(pair, o.Who, "WHO"), other = who == pair.Host ? pair.Client : pair.Host;
            bool Want(string mode) => o.Mode == "all" || o.Mode == mode;

            Picked p = await who.Run(() => InGame.Pick());
            ushort j = p.Junction;
            byte t = p.Turntable;
            Report.Note($"{o.Who} acts: junction {j} ({p.JunctionDistance} m away), turntable {t} ({p.TurntableDistance} m away)");
            long cursor = await pair.Host.LogCursor();

            if (Want("junction"))
            {
                Report.Note($"{o.Who} throws junction {j}");
                PeerResults<State> s0 = await pair.Both.Run(() => InGame.State(j, t, true));
                Require.Equal(s0.Host.Branch, s0.Client.Branch, "the client's junction branch before the test matches the host's");
                await who.Run<Acted>(s => InGame.Act(s, "junction", j, t, o.Skip), 30);
                await Task.Delay(2000);
                PeerResults<State> s1 = await pair.Both.Run(() => InGame.State(j, t, false));
                Report.Note($"branch {s0.Host.Branch} -> host {s1.Host.Branch}, client {s1.Client.Branch}; switched host {s1.Host.Switched}x, client {s1.Client.Switched}x");
                Expect.That(s1.Host.Branch != s0.Host.Branch, "the host's junction switched", $"still {s1.Host.Branch}");
                Expect.Equal(s1.Host.Branch, s1.Client.Branch, "the client's junction branch matches the host's");
                Expect.Equal(1, s1.Host.Switched, "times the host's junction switched");
                Expect.Equal(1, s1.Client.Switched, "times the client's junction switched");
            }

            if (Want("turntable"))
            {
                Require.That(t != 0, "a turntable was found");
                // A turntable only turns where the world is loaded: both players go to its controls.
                await who.Run(() => Players.TeleportToGround(p.Tx + 2f, p.Tz + 2f), 90);
                if (other == pair.Client || !pair.Dedicated) await other.Run(() => Players.TeleportToGround(p.Tx - 6f, p.Tz + 6f), 90);
                await Task.Delay(3000);
                Report.Note($"{o.Who} turns turntable {t} (players at its controls, ({p.Tx}, {p.Tz}))");
                Acted a = await who.Run<Acted>(s => InGame.Act(s, "turntable", j, t, o.Skip));
                Report.Note($"{o.Who} turned it {a.Before} -> {a.Rotation} degrees");
                Expect.That(Angle(a.Before, a.Rotation) > 1, "the turntable turned", $"{a.Before} -> {a.Rotation}");
                await Task.Delay(2000);
                PeerResults<State> s = await pair.Both.Run(() => InGame.State(j, t, false));
                Report.Note($"host {s.Host.Rotation}, client {s.Client.Rotation} degrees");
                foreach (var (peer, r) in s)
                    Expect.That(Angle(r.Rotation, a.Rotation) < 0.5, $"{peer}: the turntable's rotation matches what {o.Who} set", $"{r.Rotation}, {o.Who} set {a.Rotation}");
            }

            if (Want("fast-travel"))
            {
                Report.Note($"{o.Who} fast-travels ({o.Skip}s)");
                Require.That(p.FastTravelAdvancesTime, "valid: the host's FastTravelAdvancesTime setting is on");
                PeerResults<State> s0 = await pair.Both.Run(() => InGame.State(j, t, false));
                await who.Run<Acted>(s => InGame.Act(s, "fast-travel", j, t, o.Skip), 30);
                await Task.Delay(1000);
                PeerResults<State> s1 = await pair.Both.Run(() => InGame.State(j, t, false));
                CheckSkip("fast travel", s0, s1, o.Skip);
            }

            if (Want("sleep"))
            {
                Report.Note($"{o.Who} sleeps ({o.Skip}s)");
                // The shared cooldown must have run out, or the host refuses the client's sleep.
                if (o.Who == "client")
                    Require.AtLeast(await pair.Host.Run(() => InGame.ResetSleepCooldown()), 0, "valid: the save's SleepCooldownInHours (sleeping is disabled below 0)");
                PeerResults<State> s0 = await pair.Both.Run(() => InGame.State(j, t, false));
                Acted a = await who.Run<Acted>(s => InGame.Act(s, "sleep", j, t, o.Skip), 90);
                Require.That(!a.StillSleeping, $"{o.Who} woke up");
                await Task.Delay(1000);
                PeerResults<State> s1 = await pair.Both.Run(() => InGame.State(j, t, false));
                CheckSkip("sleep", s0, s1, o.Skip);
                Report.Note($"last wake time: host {s1.Host.Wake}, client {s1.Client.Wake}");
                Expect.That(s1.Host.Wake > s0.Host.Wake, "the host's sleep cooldown restarted", $"{s0.Host.Wake} -> {s1.Host.Wake}");
                Expect.That(Math.Abs(s1.Host.Wake - s1.Client.Wake) * 86400 < 300, "the client's sleep cooldown matches the host's (within 300 s)",
                    $"{Math.Abs(s1.Host.Wake - s1.Client.Wake) * 86400:0} s apart");
            }

            int warnings = (await pair.Host.LogsSince(cursor, "warning")).Count(l => RelayWarning.IsMatch(l.Message));
            Expect.Zero(warnings, "warnings the host logged about a relayed junction, turntable or time skip");
        }

        // Degrees between two rotations, either way round.
        private static double Angle(float a, float b)
        {
            double d = Math.Abs(a - b) % 360;
            return Math.Min(d, 360 - d);
        }

        // Each game's clock moved the skip and the few seconds of play: nowhere near a second skip.
        private static void CheckSkip(string what, PeerResults<State> before, PeerResults<State> after, int skip)
        {
            long h = Moved(before.Host, after.Host), c = Moved(before.Client, after.Client);
            Report.Note($"clocks moved: host {h}s, client {c}s (skip {skip}s)");
            foreach (var (peer, v) in new[] { ("host", h), ("client", c) })
                Expect.That(v >= skip - 60 && v < skip + Math.Max(900, skip / 4.0), $"{what}: the {peer}'s clock skipped once",
                    $"moved {v}s for a {skip}s skip (expected {skip - 60} to {skip + Math.Max(900, skip / 4.0)})");
        }

        private static long Moved(State a, State b) => (long)Math.Round((b.Clock - a.Clock) * 86400);

        public sealed class Picked
        {
            public ushort Junction;
            public byte Turntable;
            public float JunctionDistance, TurntableDistance;
            /// <summary>Where the turntable's controls are (absolute): the players go there, as a
            /// turntable only turns where the world is loaded.</summary>
            public float Tx, Tz;
            public bool FastTravelAdvancesTime;
        }

        public sealed class State
        {
            public int Branch;
            /// <summary>Times the junction switched since the last State with watch.</summary>
            public int Switched;
            public float Rotation;
            /// <summary>The game clock and the shared sleep cooldown's last wake time, as OLE dates (days).</summary>
            public double Clock, Wake;
            public bool Sleeping;
        }

        public sealed class Acted
        {
            public int Branch;
            public float Before, Rotation, Skipped;
            public bool StillSleeping;
        }

        // ---- runs inside the games
        public static class InGame
        {
            // Junction.Switched raised per junction since State(watch) last reset it.
            private static readonly Dictionary<ushort, int> switched = new();
            private static readonly HashSet<ushort> hooked = new();

            /// <summary>The acting player: the junction and the turntable nearest it, by net ID (both games
            /// number them the same way), where the turntable's controls are, and whether fast travel may
            /// skip time.</summary>
            [Step]
            public static Picked Pick()
            {
                var p = Dev.PlayerAbs.Value;
                var j = NetworkedJunction.IndexedJunctions.Where(n => n != null && n.Junction != null)
                    .OrderBy(n => Vector3.Distance(Dev.Abs(n.transform.position), p)).First();
                var t = NetworkedTurntable.IndexedTurntables.Where(n => n != null && n.TurntableRailTrack != null)
                    .OrderBy(n => Vector3.Distance(Dev.Abs(n.transform.position), p)).FirstOrDefault();
                var controller = t == null ? null : Resources.FindObjectsOfTypeAll<TurntableController>().FirstOrDefault(c => c.turntable == t.TurntableRailTrack);
                Vector3 controls = controller != null ? Dev.Abs(controller.transform.position) : t != null ? Dev.Abs(t.transform.position) : Vector3.zero;
                return new Picked
                {
                    Junction = j.NetId,
                    JunctionDistance = Mathf.Round(Vector3.Distance(Dev.Abs(j.transform.position), p)),
                    Turntable = t == null ? (byte)0 : t.NetId,
                    TurntableDistance = t == null ? -1 : Mathf.Round(Vector3.Distance(Dev.Abs(t.transform.position), p)),
                    Tx = controls.x, Tz = controls.z,
                    FastTravelAdvancesTime = Multiplayer.Client.World.TimeAdvancePatch.FastTravelAdvancesTime
                };
            }

            /// <summary>
            /// Either peer: what the scenario compares. The junction's branch and how many times it
            /// switched since <paramref name="watch"/> (each switch raises Junction.Switched once, so a
            /// change applied twice counts 2), the turntable's rotation, the game clock and the shared
            /// sleep cooldown's last wake time.
            /// </summary>
            [Step]
            public static State State(ushort junction, byte turntable, bool watch)
            {
                NetworkedJunction.Get(junction, out NetworkedJunction j);
                NetworkedTurntable.Get(turntable, out NetworkedTurntable t);
                if (j != null && watch)
                {
                    if (hooked.Add(junction))
                        j.Junction.Switched += (mode, branch) => switched[junction] = (switched.TryGetValue(junction, out int n) ? n : 0) + 1;
                    switched[junction] = 0;
                }
                var sleep = DV.BedSleepingController.Instance;
                return new State
                {
                    Branch = j == null ? -1 : (int)j.Junction.selectedBranch,
                    Switched = switched.TryGetValue(junction, out int count) ? count : 0,
                    Rotation = t == null ? -1f : t.TurntableRailTrack.targetYRotation,
                    Clock = DV.WeatherSystem.WeatherDriver.Instance.manager.RealDateTime.ToOADate(),
                    Wake = sleep == null ? 0 : sleep.lastWakeTime.ToOADate(),
                    Sleeping = sleep != null && sleep.IsSleeping
                };
            }

            /// <summary>
            /// The acting player: one action, the way the game does it. junction: throw it to its next
            /// branch; turntable: turn it half a turn with its controller; fast-travel: skip time as fast
            /// travel does; sleep: sleep in a bed. <paramref name="seconds"/>: the time skip.
            /// </summary>
            [Step]
            public static IEnumerator Act(Step<Acted> step, string action, ushort junction, byte turntable, float seconds)
            {
                if (action == "junction")
                {
                    NetworkedJunction.Get(junction, out NetworkedJunction j);
                    j.Junction.Switch(Junction.SwitchMode.REGULAR);
                    yield return new WaitForSecondsRealtime(0.5f);
                    step.Result = new Acted { Branch = j.Junction.selectedBranch };
                }
                else if (action == "turntable")
                {
                    NetworkedTurntable.Get(turntable, out NetworkedTurntable t);
                    var controller = Resources.FindObjectsOfTypeAll<TurntableController>().First(c => c.turntable == t.TurntableRailTrack);
                    float before = t.TurntableRailTrack.targetYRotation;
                    // Half a turn lines the other end up with the same tracks. Pushing the handle can't be
                    // faked: the controller's Update clears the push every frame unless a player's body is
                    // at the handle.
                    controller.SetAngle(TurntableRailTrack.AngleRange0To360(before + 180f), forceNoSnapping: true);
                    // NetworkedTurntable sends the new rotation from the next tick.
                    yield return new WaitForSecondsRealtime(1f);
                    step.Result = new Acted { Before = before, Rotation = t.TurntableRailTrack.targetYRotation };
                }
                else if (action == "fast-travel")
                {
                    DV.TimeAdvance.AdvanceTime(seconds);
                    yield return new WaitForSecondsRealtime(0.5f);
                    step.Result = new Acted { Skipped = seconds };
                }
                else if (action == "sleep")
                {
                    var sleep = DV.BedSleepingController.Instance;
                    sleep.Sleep(seconds, 0.2f, 0.2f, null);
                    yield return new WaitForSecondsRealtime(0.5f);
                    for (int i = 0; i < 60 && sleep.IsSleeping; i++)
                        yield return new WaitForSecondsRealtime(0.5f);
                    step.Result = new Acted { Skipped = seconds, StillSleeping = sleep.IsSleeping };
                }
                else
                    throw new Exception("unknown action " + action);
            }

            /// <summary>Host: the shared sleep cooldown run out. Result: the save's cooldown in hours
            /// (below 0: sleeping is disabled).</summary>
            [Step]
            public static float ResetSleepCooldown()
            {
                DV.BedSleepingController.Instance.lastWakeTime = DateTime.MinValue;
                return DV.Globals.G.GameParams.SleepCooldownInHours;
            }
        }
    }
}
