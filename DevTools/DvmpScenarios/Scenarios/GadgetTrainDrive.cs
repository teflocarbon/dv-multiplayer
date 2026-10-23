using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using DV.HUD;
using DV.Simulation.Cars;
using DV.Simulation.Controllers;
using HarmonyLib;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Trains;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A server-run ATS drives the train, and wins over the player holding a lever (gadgets.md, PR 7).
    /// The server puts a soldered ATS on a DE2 it spawns by the client, powers it, and the client
    /// rejoins, rides the loco and holds the throttle open. The server pushes the loco and lets the ATS
    /// run out: the ATS takes the throttle from the client (its lease is revoked, its hand let go),
    /// steps it down and the train brake up, in both games; on a dedicated host, with no cab loaded,
    /// by the cab lever's steps rather than in one jump. (Whether it stops is noted only: the loco's
    /// engine is off, so its brakes have no air.) The client acknowledges: the
    /// server's countdown starts again.
    /// </summary>
    [Scenario("gadget-ats", "A server-run ATS takes the throttle from the client holding it and brakes the loco in both games")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class GadgetAts : Scenario<GadgetAts.Options>
    {
        public sealed class Options
        {
            public string Livery = "LocoDE2";
            /// <summary>How long a change may take to reach the other game, in seconds.</summary>
            public float Seconds = 10f;
            /// <summary>Delay on what the client receives once it has joined, in ms.</summary>
            public int Lag;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Loco loco = await pair.Host.Run<Loco>(s => Cars.SpawnLocoNear(s, o.Livery, true, "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            GadgetDrive.Built built = await pair.Host.Run<GadgetDrive.Built>(s => GadgetDrive.Build(s, loco.Id, "ats"));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(built.PersistentIds)));
            Report.Note(built.Note);
            Require.That(built.Gadget != 0, "the server has an ATS on the loco", built.Note);
            Report.Note("host power: " + await pair.Host.Run<string>(s => GadgetButtons.InGame.Power(s, loco.Id)));
            await pair.Host.Run(() => GadgetButtons.InGame.SetSetting(built.Gadget, "mode", 1f));

            await GadgetDrive.RejoinAndHoldThrottle(pair, loco.Id, 0.6f);
            using (await pair.Client.Lag(o.Lag))
            {
                GadgetDrive.Drive host = await pair.Host.Run<GadgetDrive.Drive>(s => GadgetDrive.TripAts(s, loco.Id, built.Gadget, 4f), 60);
                Report.Note("server: " + host);
                Expect.That(host.ThrottleBefore > 0.5f, "the client held the throttle open on the server", host.ToString());
                Expect.That(host.HeldBefore != 0 && host.HeldAfter == 0, "the ATS braked: the client's throttle lease, taken", host.ToString());
                Expect.That(host.ThrottleAfter < host.ThrottleBefore - 0.05f, "the ATS braked: the server's throttle, stepped down", host.ToString());
                Expect.That(host.BrakeAfter > host.BrakeBefore + 0.05f, "the ATS braked: the server's train brake, stepped up", host.ToString());
                Expect.That(host.LargestStep < 0.99f, "the ATS braked: the brake moved in steps, not one jump", host.ToString());
                GadgetDrive.Drive client = await pair.Client.Run<GadgetDrive.Drive>(s => GadgetDrive.ControlsWhen(s, loco.Id, host.ThrottleAfter, host.BrakeAfter, o.Seconds));
                Expect.That(Math.Abs(client.ThrottleAfter - host.ThrottleAfter) < 0.05f && Math.Abs(client.BrakeAfter - host.BrakeAfter) < 0.05f,
                    "the ATS braked: the client's throttle and brake, against the server's", $"client {client}; server {host}");
                Expect.That(!client.OwnsThrottle, "the ATS braked: the client no longer holds the throttle", client.ToString());
                Report.Note($"speed 30 s later (no air for the brakes): server {await pair.Host.Run<float>(s => Cars.WaitUntilStopped(s, loco.Id, 30f), 45):0.00} m/s, " +
                            $"client {await pair.Client.Run<float>(s => Cars.WaitUntilStopped(s, loco.Id, 10f), 25):0.00} m/s");

                await pair.Client.Run(() => GadgetButtons.InGame.Press(built.Gadget, "ats"));
                float length = await pair.Host.Run<float>(() => GadgetButtons.InGame.AtsLength(built.Gadget));
                Expect.Equal($"timer={length:0}", await pair.Host.Run<string>(s => GadgetButtons.InGame.RunningWhen(s, built.Gadget, "timer", length, 1.5f, o.Seconds)),
                    "the client acknowledged the ATS: the server's countdown");
            }
        }
    }

    /// <summary>
    /// A server-run anti-slip eases the throttle and sands while the wheels slip (gadgets.md, PR 7). The
    /// server puts a soldered anti-slip on a DE2 it spawns by the client and powers it; the client
    /// rejoins, rides the loco and holds the throttle open. A server step makes the loco's wheels slip
    /// for a few seconds: the anti-slip takes the throttle from the client and steps it down, runs the
    /// sander, and lights its lamp, on the server and in the client's game.
    /// </summary>
    [Scenario("gadget-antislip", "A server-run anti-slip takes the throttle from the client, steps it down and sands in both games while the wheels slip")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class GadgetAntiSlipDrive : Scenario<GadgetAntiSlipDrive.Options>
    {
        public sealed class Options
        {
            public string Livery = "LocoDE2";
            public float Seconds = 10f;
            public int Lag;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Loco loco = await pair.Host.Run<Loco>(s => Cars.SpawnLocoNear(s, o.Livery, true, "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            GadgetDrive.Built built = await pair.Host.Run<GadgetDrive.Built>(s => GadgetDrive.Build(s, loco.Id, "antislip"));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(built.PersistentIds)));
            Report.Note(built.Note);
            Require.That(built.Gadget != 0, "the server has an anti-slip on the loco", built.Note);
            Report.Note("host power: " + await pair.Host.Run<string>(s => GadgetButtons.InGame.Power(s, loco.Id)));

            await GadgetDrive.RejoinAndHoldThrottle(pair, loco.Id, 0.8f);
            using (await pair.Client.Lag(o.Lag))
            {
                GadgetDrive.Drive host = await pair.Host.Run<GadgetDrive.Drive>(s => GadgetDrive.Slip(s, loco.Id, built.Gadget, 3f), 60);
                Report.Note("server: " + host);
                Expect.That(host.HeldBefore != 0 && host.HeldAfter == 0, "the wheels slipped: the client's throttle lease, taken", host.ToString());
                Expect.That(host.ThrottleAfter < host.ThrottleBefore - 0.05f, "the wheels slipped: the server's throttle, stepped down", host.ToString());
                Expect.That(host.Sander > 0.5f, "the wheels slipped: the server's sander, on", host.ToString());
                Expect.That(host.Active, "the wheels slipped: the server's anti-slip, active", host.ToString());
                Expect.Equal("active=1", await pair.Client.Run<string>(s => GadgetButtons.InGame.RunningWhen(s, built.Gadget, "active", 1f, 0f, o.Seconds)),
                    "the wheels slipped: the client's anti-slip lamp");
                GadgetDrive.Drive client = await pair.Client.Run<GadgetDrive.Drive>(s => GadgetDrive.SanderWhen(s, loco.Id, o.Seconds));
                Expect.That(client.Sander > 0.5f, "the wheels slipped: the client's sander", client.ToString());
                Expect.That(!client.OwnsThrottle, "the wheels slipped: the client no longer holds the throttle", client.ToString());
            }
        }
    }

    /// <summary>Steps the train-driving gadget scenarios share.</summary>
    public static class GadgetDrive
    {
        public sealed class Built
        {
            public ushort Gadget;
            public string[] PersistentIds;
            public string Note;
        }

        /// <summary>What the controls did: before and after, the largest single move of the brake, and
        /// who held the throttle (a player id, 0 for nobody).</summary>
        public sealed class Drive
        {
            public float ThrottleBefore, ThrottleAfter, BrakeBefore, BrakeAfter, LargestStep, Sander;
            public byte HeldBefore, HeldAfter;
            public bool Active, OwnsThrottle;
            public override string ToString() =>
                $"throttle {ThrottleBefore:0.###}->{ThrottleAfter:0.###}, brake {BrakeBefore:0.###}->{BrakeAfter:0.###} " +
                $"(largest step {LargestStep:0.###}), sander {Sander:0.##}, throttle held by {HeldBefore}->{HeldAfter}, " +
                $"active {Active}, owns throttle {OwnsThrottle}";
        }

        /// <summary>The client joins again (to get the server's gadgets), rides the loco and holds its
        /// throttle at <paramref name="value"/>.</summary>
        public static async Task RejoinAndHoldThrottle(Pair pair, string loco, float value)
        {
            await pair.Client.Quit();
            await Task.Delay(4000);   // the host notices the departure
            await pair.Rejoin();
            string host = await pair.Host.Run<string>(() => Gadgets.Digest(loco));
            Expect.Equal(host, await pair.Client.Run<string>(s => Gadgets.DigestWhenEqual(s, loco, host, 20f)),
                "after joining: the client's gadgets on the loco, against the server's");
            await pair.Client.Run(() => Players.Board(loco));
            await Task.Delay(4000);   // the cab loads and hooks its controls
            Drive held = await pair.Client.Run<Drive>(s => HoldThrottle(s, loco, value), 30);
            Report.Note("client: " + held);
            Require.That(held.OwnsThrottle, "the client holds the throttle", held.ToString());
        }

        /// <summary>Host: one gadget of <paramref name="kind"/> ("ats", "antislip") on the loco, soldered.</summary>
        [Step]
        public static IEnumerator Build(Step<Built> step, string carId, string kind)
        {
            var client = Gadgets.Client();
            TrainCar car = Dvmp.Lib.Trains.Find(carId);
            Func<GadgetBase, bool> fits = kind == "ats" ? g => g is GadgetATS
                : kind == "antislip" ? g => g is GadgetAntiSlip
                : throw new ArgumentException("no gadget kind " + kind);
            InventoryItemSpec spec = DV.Globals.G.Items.items
                .Where(s => s != null && s.GetComponent<GadgetItem>()?.gadgetPrefab is { } g && fits(g))
                .OrderBy(s => s.ItemPrefabName, StringComparer.Ordinal).FirstOrDefault();
            if (spec == null)
            {
                step.Result = new Built { PersistentIds = new string[0], Note = $"no {kind} in this game" };
                yield break;
            }
            NetworkedItem item = null;
            yield return Gadgets.SpawnOwned(spec, client.WorldPosition + Vector3.right * 1.5f + Vector3.up, client, i => item = i);
            GadgetBase gadget = Gadgets.Install(item, car.Customization, car.interior.TransformPoint(new Vector3(0.3f, 1.5f, 0f)));
            if (gadget is TrainCarCustomization.TrainCarCustomizerBase c && c.IsSolderable)
                c.MakeSoldered();
            for (int i = 0; i < 10; i++) yield return null;
            step.Result = new Built
            {
                Gadget = gadget != null && gadget.IsLinked ? item.NetId : (ushort)0,
                PersistentIds = new[] { item.GetComponent<Multiplayer.Server.Items.SaveLoad.ItemIdentity>().Value.ToString("D") },
                Note = $"{spec.ItemPrefabName} on {carId}: linked {gadget != null && gadget.IsLinked}"
            };
        }

        private static BaseControlsOverrider Controls(TrainCar car) => car.SimController.controlsOverrider;

        private static byte ThrottleHolder(TrainCar car)
        {
            NetworkedTrainCar.TryGetFromTrainCar(car, out NetworkedTrainCar networked);
            uint port = NetworkedTrainCar.GetPortNetId(Controls(car).Throttle.portId);
            return Multiplayer.Server.Trains.NetworkedTrainCarServer.Of(networked).LeaseHolder(port)?.PlayerId ?? 0;
        }

        private static bool OwnsThrottle(TrainCar car)
        {
            NetworkedTrainCar.TryGetFromTrainCar(car, out NetworkedTrainCar networked);
            uint port = NetworkedTrainCar.GetPortNetId(Controls(car).Throttle.portId);
            // A dedicated host's cars have no client part: nobody there holds anything.
            return Multiplayer.Client.Trains.NetworkedTrainCarClient.Of(networked)?.locallyOwnedPortAuthorities.Contains(port) == true;
        }

        private static Drive Read(TrainCar car) => new()
        {
            ThrottleAfter = Controls(car).Throttle.Value,
            BrakeAfter = Controls(car).Brake.Value,
            Sander = Controls(car).Sander?.Value ?? -1f,
            OwnsThrottle = OwnsThrottle(car)
        };

        /// <summary>Client: grabs the cab's throttle (the game's own Grabbed event) and opens it to
        /// <paramref name="value"/>, and keeps holding it.</summary>
        [Step]
        public static IEnumerator HoldThrottle(Step<Drive> step, string loco, float value)
        {
            TrainCar car = Dvmp.Lib.Trains.Find(loco);
            NetworkedTrainCar.TryGetFromTrainCar(car, out NetworkedTrainCar networked);
            var client = Multiplayer.Client.Trains.NetworkedTrainCarClient.Of(networked);
            InteriorControlsManager.ControlReference throttle = default;
            // The cab loads, then hooks its controls for leases: a grab before that takes none.
            for (float end = Time.realtimeSinceStartup + 20f; Time.realtimeSinceStartup < end; )
            {
                InteriorControlsManager manager = car.loadedInterior != null ? car.loadedInterior.GetComponent<InteriorControlsManager>() : null;
                if (manager != null && manager.TryGetControl(InteriorControlsManager.ControlType.Throttle, out throttle) &&
                    throttle.controlImplBase != null && client.controlToPortNetId.ContainsKey(throttle.controlImplBase))
                    break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            throttle.controlImplBase.FireGrabbed();
            yield return new WaitForSecondsRealtime(0.5f);
            throttle.controlImplBase.SetValue(value);
            yield return new WaitForSecondsRealtime(2f);   // the lease answered, the value sent
            step.Result = Read(car);
        }

        /// <summary>
        /// Host: pushes the loco and lets the ATS run past its warning, then watches the controls for
        /// <paramref name="seconds"/> while it brakes. The push is a coroutine of its own that ends by
        /// itself; the ATS's brake stops the loco once it does.
        /// </summary>
        [Step]
        public static IEnumerator TripAts(Step<Drive> step, string loco, ushort atsNetId, float seconds)
        {
            TrainCar car = Dvmp.Lib.Trains.Find(loco);
            var ats = (GadgetATS)Gadget(atsNetId);
            // Held by its handbrake until now, so it didn't roll and run the ATS out during the setup. A
            // loco spawns with its train brake applied: off, so the ATS has a brake to step up.
            foreach (TrainCar c in car.trainset.cars) c.brakeSystem.SetHandbrakePosition(0f);
            Controls(car).Brake.Set(0f);
            yield return new WaitForSecondsRealtime(0.5f);
            Drive drive = new() { ThrottleBefore = Controls(car).Throttle.Value, BrakeBefore = Controls(car).Brake.Value, HeldBefore = ThrottleHolder(car) };
            CoroutineManager.Instance.StartCoroutine(Dvmp.Lib.Trains.Push(car, 3f, seconds + 2f, 1f));
            float until = Time.realtimeSinceStartup + 5f;
            while (Mathf.Abs(car.GetForwardSpeed()) < 1f && Time.realtimeSinceStartup < until)
                yield return null;
            ats.currentTimer = -(ats.warningTime + 0.1f);
            float last = Controls(car).Brake.Value;
            for (float end = Time.realtimeSinceStartup + seconds; Time.realtimeSinceStartup < end;)
            {
                yield return null;
                float brake = Controls(car).Brake.Value;
                drive.LargestStep = Mathf.Max(drive.LargestStep, Mathf.Abs(brake - last));
                last = brake;
            }
            Drive now = Read(car);
            drive.ThrottleAfter = now.ThrottleAfter;
            drive.BrakeAfter = now.BrakeAfter;
            drive.Sander = now.Sander;
            drive.HeldAfter = ThrottleHolder(car);
            step.Result = drive;
        }

        // The wheels of this anti-slip's car slip while it is set: set just before vanilla's Update reads it.
        private static GadgetAntiSlip slipping;

        private static void SlipPrefix(GadgetAntiSlip __instance)
        {
            if (__instance == slipping && __instance.TrainCar.adhesionController.wheelslipController.IsSome(out var controller))
                controller.wheelslip = 1f;
        }

        /// <summary>Host: the loco's wheels slip for <paramref name="seconds"/>, as the anti-slip sees them.</summary>
        [Step]
        public static IEnumerator Slip(Step<Drive> step, string loco, ushort antiSlipNetId, float seconds)
        {
            TrainCar car = Dvmp.Lib.Trains.Find(loco);
            var antiSlip = (GadgetAntiSlip)Gadget(antiSlipNetId);
            Drive drive = new() { ThrottleBefore = Controls(car).Throttle.Value, BrakeBefore = Controls(car).Brake.Value, HeldBefore = ThrottleHolder(car) };
            var harmony = new Harmony("dvmp.scenario.gadget-antislip");
            harmony.Patch(AccessTools.Method(typeof(GadgetAntiSlip), "Update"), prefix: new HarmonyMethod(typeof(GadgetDrive), nameof(SlipPrefix)));
            slipping = antiSlip;
            try
            {
                for (float end = Time.realtimeSinceStartup + seconds; Time.realtimeSinceStartup < end;)
                {
                    yield return null;
                    drive.Active |= antiSlip.IsActivated;
                }
            }
            finally
            {
                slipping = null;
                harmony.UnpatchAll("dvmp.scenario.gadget-antislip");
            }
            Drive now = Read(car);
            drive.ThrottleAfter = now.ThrottleAfter;
            drive.BrakeAfter = now.BrakeAfter;
            drive.Sander = now.Sander;
            drive.HeldAfter = ThrottleHolder(car);
            step.Result = drive;
        }

        /// <summary>Client: its throttle and brake once they're within 0.05 of the server's, or after <paramref name="seconds"/>.</summary>
        [Step]
        public static IEnumerator ControlsWhen(Step<Drive> step, string loco, float throttle, float brake, float seconds)
        {
            TrainCar car = Dvmp.Lib.Trains.Find(loco);
            float end = Time.realtimeSinceStartup + seconds;
            do
            {
                step.Result = Read(car);
                if (Mathf.Abs(step.Result.ThrottleAfter - throttle) < 0.05f && Mathf.Abs(step.Result.BrakeAfter - brake) < 0.05f)
                    yield break;
                yield return new WaitForSecondsRealtime(0.25f);
            } while (Time.realtimeSinceStartup < end);
        }

        /// <summary>Client: its controls once its sander is on, or after <paramref name="seconds"/>.</summary>
        [Step]
        public static IEnumerator SanderWhen(Step<Drive> step, string loco, float seconds)
        {
            TrainCar car = Dvmp.Lib.Trains.Find(loco);
            float end = Time.realtimeSinceStartup + seconds;
            do
            {
                step.Result = Read(car);
                if (step.Result.Sander > 0.5f)
                    yield break;
                yield return new WaitForSecondsRealtime(0.25f);
            } while (Time.realtimeSinceStartup < end);
        }

        private static GadgetBase Gadget(ushort netId) =>
            NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null
                ? item.GetComponent<GadgetItem>()?.Gadget
                : null;
    }
}
