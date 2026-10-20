using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The client in each loco's cab: its cab reloads and, half a second later, it grabs a lever,
    /// which must be hooked, and the grab must take its lease. Before 2026-10-20 a cab with a control
    /// the old ready check never passed hooked nothing for 2 s; the run says which cabs have one.
    /// The cab stays unloaded 2.5 s and loads: no "failed to load an interior" (logged on every such
    /// unload before). It reloads three times in quick succession, each unloaded mid-hook: no hook
    /// pass throws, and every control ends up hooked. Was cab-controls MODE=cab-reload.
    /// </summary>
    [Scenario("cab-reload", "The client's cab reloads, quickly and slowly: its levers are hooked in time, a grab takes the lease, and no hook pass throws or logs a failed interior load")]
    [Budget(900)]
    public sealed class CabReload : Scenario<CabReload.Options>
    {
        public sealed class Options
        {
            public string[] Locos = { "LocoS282A", "LocoDE2", "LocoDM3" };
        }

        // The client's hook pass going wrong: an exception in it, or the interior failing to load.
        private static readonly Regex HookError = new("HookControls|HookInteriorControls|WaitForInterior");

        protected override async Task Run(Pair pair, Options o)
        {
            List<string> exercised = new();
            foreach (string type in o.Locos)
            {
                Report.Note("== " + type);
                // Held still: a loco left free rolls away on a grade and leaves the client too far
                // away for the host to take its changes.
                HeldLoco loco = await pair.Host.Run<HeldLoco>(s => Cars.SpawnHeldLoco(s, type), 150);
                bool deleted = false;
                Func<Task> delete = async () =>
                {
                    if (deleted) return;
                    deleted = true;
                    await pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars));
                };
                pair.Cleanup(delete);
                await pair.Client.Run(() => Players.Board(loco.Loco));
                await Task.Delay(4000);   // the cab settles after boarding, as before
                long cursor = await pair.Client.LogCursor();

                // The 2 s window: a cab with a control the old ready check never passed hooked
                // nothing for 2 s. That needs such a control on this cab; reported where it has none.
                Reloaded r = await pair.Client.Run<Reloaded>(s => InGame.Reload(s, loco.Loco, 0.5f, 0, 0f));
                if (r.OldWait.Length > 0)
                {
                    Report.Note("the old hook pass waited 2 s on this cab for: " + string.Join(",", r.OldWait));
                    exercised.Add(type);
                }
                else Report.Note("no control on this cab kept the old hook pass waiting");
                ExpectLeased(r, $"{type}: reloaded, grabbed 0.5 s later: hooked in time, the grab took the lease, given back on release");

                r = await pair.Client.Run<Reloaded>(s => InGame.Reload(s, loco.Loco, 2.5f, 0, 2.5f));
                ExpectLeased(r, $"{type}: unloaded 2.5 s, then loaded: hooked and leased");

                r = await pair.Client.Run<Reloaded>(s => InGame.Reload(s, loco.Loco, 2.5f, 3, 0f));
                ExpectLeased(r, $"{type}: reloaded three times in quick succession, each unloaded mid-hook: the last cab's lever hooked and leased");

                Hooked h = await pair.Client.Run(() => InGame.Hooked(loco.Loco));
                Report.Note($"{h.Feeders} port-fed controls");
                Expect.Empty(h.Unhooked, $"{type}: unhooked controls");

                await Task.Delay(3000);   // a stale hook pass from the quick reloads throws once its 2 s are up
                LogLine[] errors = (await pair.Client.LogsSince(cursor, "error"))
                    .Where(l => l.Level == "exception" && HookError.IsMatch(l.Message + "\n" + l.Stack)
                                || l.Level == "error" && l.Message.IndexOf("failed to load an interior", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToArray();
                // Without the fix the 2.5 s unload alone logs "failed to load an interior": this fails on any cab.
                Expect.Empty(errors.Select(e => e.Message), $"{type}: hook errors or exceptions in the client's log ('failed to load an interior', HookControls)");

                await pair.Client.Run(() => Players.TeleportToGround(7190f, 6880f));
                await delete();
            }
            Report.Note("the 2 s window was exercised on: " + (exercised.Count > 0 ? string.Join(" ", exercised) : "none of " + string.Join(" ", o.Locos)));
        }

        private static void ExpectLeased(Reloaded r, string what) =>
            Expect.That(r.Hooked && r.Owned && r.Released, what, $"lever {r.Lever}: hooked {r.Hooked}, owned {r.Owned}, released {r.Released}");

        public sealed class Reloaded
        {
            public string Lever;
            /// <summary>This cab's controls the old ready check failed on.</summary>
            public string[] OldWait;
            public bool Hooked, Owned, Released;
        }

        public sealed class Hooked
        {
            public int Feeders;
            public string[] Unhooked;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Client: reloads the loco's cab interior, as walking out of and back into its load
            /// distance does: first <paramref name="quick"/> times in a row with the cab unloaded
            /// again 0.3 s after each load (its hook pass then still running), then once more after
            /// leaving it unloaded <paramref name="gap"/> seconds. <paramref name="after"/> seconds after
            /// the last load it grabs a lever, as a player reaching for one right after the cab
            /// appears does, checks it took the lever's lease, and lets go. Before 2026-10-20 a cab with
            /// a control the old ready check never passed hooked nothing for 2 s, so that grab took no
            /// lease; a cab unloaded within those 2 s threw in HookControls; and one left unloaded 2 s
            /// logged "failed to load an interior". OldWait lists this cab's controls that check fails
            /// on: no implementation, or no port while it has an overrider or a feeder.
            /// </summary>
            [Step]
            public static IEnumerator Reload(Step<Reloaded> step, string loco, float after, int quick, float gap)
            {
                var car = Trains.Find(loco);
                var client = Multiplayer.Client.Trains.NetworkedTrainCarClient.Of(car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>());
                IEnumerator Load()
                {
                    // The game's own load distance check may have loaded it again already.
                    for (int i = 0; i < 3 && car.loadedInterior == null; i++)
                        yield return null;
                    if (car.loadedInterior == null)
                        car.LoadInterior();
                    if (car.loadedInterior == null)
                        throw new Exception("the cab of " + car.ID + " didn't load again");
                }
                for (int i = 0; i < quick; i++)
                {
                    car.UnloadInterior();
                    yield return Load();
                    yield return new WaitForSecondsRealtime(0.3f);   // unloaded again mid-hook: the condition under test
                }
                car.UnloadInterior();
                if (gap > 0)
                {
                    // Kept unloaded: the game would load it again at once with the player this close.
                    car.blockInteriorLoading = true;
                    yield return new WaitForSecondsRealtime(gap);
                    car.blockInteriorLoading = false;
                }
                yield return Load();

                string PortOf(DV.HUD.InteriorControlsManager.ControlReference r) =>
                    !string.IsNullOrEmpty(r.overridableBaseControl?.portId) ? r.overridableBaseControl.portId
                    : r.controlImplBase?.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>()?.portId;
                var manager = car.loadedInterior.GetComponent<DV.HUD.InteriorControlsManager>();
                // Wait for the cab to make its controls (a frame or two), as the mod does.
                for (int i = 0; i < 10 && manager != null && !manager.Initialized; i++) yield return null;
                var refs = manager != null ? manager.controls.Values.ToList() : new List<DV.HUD.InteriorControlsManager.ControlReference>();
                // The old check (NetworkedTrainCarClient.ControlReady before 2026-10-20), word for word.
                bool OldReady(DV.HUD.InteriorControlsManager.ControlReference r) =>
                    r.controlImplBase != null && (!string.IsNullOrEmpty(PortOf(r)) ||
                        (r.overridableBaseControl == null && r.controlImplBase.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>() == null));
                var oldWait = manager == null ? new List<string>() : manager.controls.Where(kv => !OldReady(kv.Value)).Select(kv => kv.Key.ToString()).ToList();
                // A lever or a knob that feeds a port: taking hold of it asks for its lease.
                var lever = refs.Where(r => r.controlImplBase is DV.CabControls.LeverBase or DV.CabControls.RotaryBase && !string.IsNullOrEmpty(PortOf(r)))
                    .Select(r => r.controlImplBase).FirstOrDefault()
                    ?? throw new Exception("no lever with a port in the cab of " + car.ID);
                uint netId = Multiplayer.Shared.Trains.NetworkedTrainCar.GetPortNetId(PortOf(refs.First(r => r.controlImplBase == lever)));
                float sinceLoad = 0;
                while (sinceLoad < after) { yield return null; sinceLoad += Time.unscaledDeltaTime; }
                bool hooked = client.controlToPortNetId.ContainsKey(lever);
                lever.FireGrabbed();
                yield return new WaitForSecondsRealtime(0.5f);
                bool owned = client.locallyOwnedPortAuthorities.Contains(netId);
                lever.FireUngrabbed();
                // A sprung control is let go once it has settled (polled every 0.5 s), then the lease goes.
                yield return new WaitForSecondsRealtime(3f);
                step.Result = new Reloaded { Lever = lever.name, OldWait = oldWait.ToArray(), Hooked = hooked, Owned = owned, Released = !client.locallyOwnedPortAuthorities.Contains(netId) };
            }

            /// <summary>
            /// Client: every cab control that feeds a sim port must be hooked, or the client never
            /// takes its lease and drops every change to it (#73). A port another control already
            /// drives (a steam loco's whistle rope owns the whistle's dummy lever) counts as hooked,
            /// and so does every port of a control that feeds several.
            /// </summary>
            [Step]
            public static Hooked Hooked(string loco)
            {
                var car = Trains.Find(loco);
                var ntc = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>();
                if (car.loadedInterior == null) return new Hooked { Unhooked = new[] { "no interior loaded" } };
                var feeders = car.loadedInterior.GetComponentsInChildren<DV.Simulation.Ports.InteractablePortFeeder>(true);
                var unhooked = feeders.Where(f => !Multiplayer.Client.Trains.NetworkedTrainCarClient.Of(ntc).portNetIdToControl.ContainsKey(Multiplayer.Shared.Trains.NetworkedTrainCar.GetPortNetId(f.portId)))
                    .Select(f => f.name + " (" + f.portId + ")").ToArray();
                return new Hooked { Feeders = feeders.Length, Unhooked = unhooked };
            }
        }
    }
}
