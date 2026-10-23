using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using DV.HUD;
using Multiplayer.Core.Gadgets;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Trains;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Wireless MU through DPUs is the same in every game (gadgets.md, PR 8). The server puts a soldered
    /// DPU on two DE2s, one by the client set to transmit on channel 3 and one by the host's player set
    /// to receive on it, and powers both; the client rejoins, rides the transmitter and opens its
    /// throttle. The receiver's throttle follows on the server and in the client's game, and the
    /// receiver's throttle is blocked in the client's game, as vanilla's radio blocks it.
    /// </summary>
    [Scenario("gadget-dpu", "A DPU transmitter's throttle moves its receiver in every game, and the receiver's controls are blocked")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class GadgetDpu : Scenario<GadgetDpu.Options>
    {
        public sealed class Options
        {
            public string Livery = "LocoDE2";
            public float Seconds = 10f;
            public int Lag;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Loco transmitter = await pair.Host.Run<Loco>(s => Cars.SpawnLocoNear(s, o.Livery, true, "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, transmitter.Cars)));
            Loco receiver = await pair.Host.Run<Loco>(s => Cars.SpawnLocoNear(s, o.Livery, true, "host"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, receiver.Cars)));
            GadgetDrive.Built tx = await pair.Host.Run<GadgetDrive.Built>(s => GadgetPanels.Build(s, transmitter.Id, "dpu"));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(tx.PersistentIds)));
            GadgetDrive.Built rx = await pair.Host.Run<GadgetDrive.Built>(s => GadgetPanels.Build(s, receiver.Id, "dpu"));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(rx.PersistentIds)));
            Report.Note(tx.Note + "; " + rx.Note);
            Require.That(tx.Gadget != 0 && rx.Gadget != 0, "the server has a DPU on each loco", tx.Note + "; " + rx.Note);
            Report.Note("host power: " + await pair.Host.Run<string>(s => GadgetButtons.InGame.Power(s, transmitter.Id)) +
                        "; " + await pair.Host.Run<string>(s => GadgetButtons.InGame.Power(s, receiver.Id)));
            await pair.Host.Run(() => GadgetPanels.Dpu(tx.Gadget, 3, 0));
            await pair.Host.Run(() => GadgetPanels.Dpu(rx.Gadget, 3, 2));

            await GadgetDrive.RejoinAndHoldThrottle(pair, transmitter.Id, 0.7f);
            using (await pair.Client.Lag(o.Lag))
            {
                string host = await pair.Host.Run<string>(s => GadgetPanels.ThrottleWhen(s, receiver.Id, 0.7f, o.Seconds));
                Expect.Equal("throttle=0.7", host, "the client opened the transmitter's throttle: the server's receiver");
                string client = await pair.Client.Run<string>(s => GadgetPanels.ThrottleWhen(s, receiver.Id, 0.7f, o.Seconds));
                Expect.Equal("throttle=0.7", client, "the client opened the transmitter's throttle: the client's receiver");
                Expect.That(await pair.Client.Run<bool>(() => GadgetPanels.ThrottleBlocked(receiver.Id)),
                    "the receiver's throttle is blocked in the client's game");
            }
        }
    }

    /// <summary>
    /// A control panel's joysticks drive its car through the server, and its rain fault is the same in
    /// every game (gadgets.md, PR 8). The server puts a soldered control panel in a DE2 by the client and
    /// powers it; the client rejoins and rides it.
    /// - The client pushes the panel's throttle joystick three times: the server's throttle moves up by
    ///   steps, not to full, and the client's follows.
    /// - On a player host, while the host's player holds the throttle lever, the client's joystick does
    ///   nothing.
    /// - With a 50 % chance of rain fault, both games fault on the same clock steps.
    /// </summary>
    [Scenario("gadget-control-panel", "A control panel's joystick moves its car's throttle by steps through the server; its rain fault is the same in every game")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class GadgetControlPanelDrive : Scenario<GadgetControlPanelDrive.Options>
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
            GadgetDrive.Built built = await pair.Host.Run<GadgetDrive.Built>(s => GadgetPanels.Build(s, loco.Id, "panel"));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(built.PersistentIds)));
            Report.Note(built.Note);
            Require.That(built.Gadget != 0, "the server has a control panel in the loco", built.Note);
            Report.Note("host power: " + await pair.Host.Run<string>(s => GadgetButtons.InGame.Power(s, loco.Id)));

            await pair.Client.Quit();
            await Task.Delay(4000);   // the host notices the departure
            await pair.Rejoin();
            string digest = await pair.Host.Run<string>(() => Gadgets.Digest(loco.Id));
            Expect.Equal(digest, await pair.Client.Run<string>(s => Gadgets.DigestWhenEqual(s, loco.Id, digest, 20f)),
                "after joining: the client's gadgets on the loco, against the server's");
            await pair.Client.Run(() => Players.Board(loco.Id));
            await Task.Delay(2000);
            Require.That(await pair.Client.Run<int>(s => GadgetKnobs.InGame.KnobsWhenLoaded(s, built.Gadget, 10f)) > 0,
                "the client's control panel is loaded (it rides the loco)");

            using (await pair.Client.Lag(o.Lag))
            {
                float before = await pair.Host.Run<float>(() => GadgetPanels.Throttle(loco.Id));
                await pair.Client.Run(() => GadgetPanels.PushThrottle(built.Gadget, 3));
                float after = await pair.Host.Run<float>(s => GadgetPanels.ThrottleAbove(s, loco.Id, before + 0.01f, o.Seconds));
                Report.Note($"server throttle {before:0.###} -> {after:0.###}");
                Expect.That(after > before + 0.01f, "the client pushed the panel's throttle joystick: the server's throttle went up", $"{before:0.###} -> {after:0.###}");
                Expect.That(after < before + 0.75f, "the client pushed the panel's throttle joystick three times: by steps, not to full", $"{before:0.###} -> {after:0.###}");
                Expect.Equal($"throttle={after:0.##}", await pair.Client.Run<string>(s => GadgetPanels.ThrottleWhen(s, loco.Id, after, o.Seconds)),
                    "the client pushed the panel's throttle joystick: the client's throttle");

                if (!pair.Dedicated)
                {
                    await pair.Host.Run(() => Players.Board(loco.Id));
                    await pair.Host.Run(() => GadgetPanels.HostHoldsThrottle(loco.Id, true));
                    await Task.Delay(1500);
                    float held = await pair.Host.Run<float>(() => GadgetPanels.Throttle(loco.Id));
                    await pair.Client.Run(() => GadgetPanels.PushThrottle(built.Gadget, 2));
                    await Task.Delay(3000);
                    Expect.Equal($"{held:0.###}", $"{await pair.Host.Run<float>(() => GadgetPanels.Throttle(loco.Id)):0.###}",
                        "the host's player holds the throttle lever: the client's joystick leaves the server's throttle");
                    await pair.Host.Run(() => GadgetPanels.HostHoldsThrottle(loco.Id, false));
                }

                long hostStep = await pair.Host.Run<long>(() => GadgetPanels.FaultStep());
                long clientStep = await pair.Client.Run<long>(() => GadgetPanels.FaultStep());
                Report.Note($"fault clock: server step {hostStep}, client step {clientStep}");
                Expect.That(Math.Abs(hostStep - clientStep) <= 4 + o.Lag / 125, "the panels' fault clock, the same in both games to a few steps",
                    $"server {hostStep}, client {clientStep}");
                string hostFaults = await pair.Host.Run<string>(() => GadgetPanels.Faults(built.Gadget, hostStep, 64, 0.5f));
                Expect.Equal(hostFaults, await pair.Client.Run<string>(() => GadgetPanels.Faults(built.Gadget, hostStep, 64, 0.5f)),
                    "with a 50 % chance of rain fault: the steps the client's panel faults on, against the server's");
                Report.Note($"rain: server {await pair.Host.Run<float>(() => GadgetPanels.Rain(built.Gadget)):0.##}, " +
                            $"client {await pair.Client.Run<float>(() => GadgetPanels.Rain(built.Gadget)):0.##}");
            }
        }
    }

    /// <summary>Steps the DPU and control panel scenarios share.</summary>
    public static class GadgetPanels
    {
        private static GadgetBase Gadget(ushort netId) =>
            NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null
                ? item.GetComponent<GadgetItem>()?.Gadget
                : null;

        /// <summary>Host: one gadget of <paramref name="kind"/> ("dpu", "panel") in the loco's cab, soldered.</summary>
        [Step]
        public static IEnumerator Build(Step<GadgetDrive.Built> step, string carId, string kind)
        {
            var client = Gadgets.Client();
            TrainCar car = Dvmp.Lib.Trains.Find(carId);
            Func<GadgetBase, bool> fits = kind == "dpu" ? g => g is GadgetDPU
                : kind == "panel" ? g => g is GadgetControlPanel
                : throw new ArgumentException("no gadget kind " + kind);
            InventoryItemSpec spec = DV.Globals.G.Items.items
                .Where(s => s != null && s.GetComponent<GadgetItem>()?.gadgetPrefab is { } g && fits(g))
                .OrderBy(s => s.ItemPrefabName, StringComparer.Ordinal).FirstOrDefault();
            if (spec == null)
            {
                step.Result = new GadgetDrive.Built { PersistentIds = new string[0], Note = $"no {kind} in this game" };
                yield break;
            }
            NetworkedItem item = null;
            yield return Gadgets.SpawnOwned(spec, client.WorldPosition + Vector3.right * 1.5f + Vector3.up, client, i => item = i);
            GadgetBase gadget = Gadgets.Install(item, car.Customization, car.interior.TransformPoint(new Vector3(0.3f, 1.5f, 0f)));
            if (gadget is TrainCarCustomization.TrainCarCustomizerBase c && c.IsSolderable)
                c.MakeSoldered();
            for (int i = 0; i < 10; i++) yield return null;
            step.Result = new GadgetDrive.Built
            {
                Gadget = gadget != null && gadget.IsLinked ? item.NetId : (ushort)0,
                PersistentIds = new[] { item.GetComponent<Multiplayer.Server.Items.SaveLoad.PersistentWorldItemIdentity>().Value.ToString("D") },
                Note = $"{spec.ItemPrefabName} on {carId}: linked {gadget != null && gadget.IsLinked}"
            };
        }

        /// <summary>The panels' fault clock step in this game now.</summary>
        [Step]
        public static long FaultStep() => GadgetControlPanels.Step();

        /// <summary>Host: a DPU's channel and regime (0 transmit, 1 off, 2 receive), as its knobs set them.</summary>
        [Step]
        public static void Dpu(ushort netId, int channel, int regime) =>
            GadgetSettings.Apply(Gadget(netId), new[] { "channel", "regime" }, new[] { (float)channel, regime });

        [Step]
        public static float Throttle(string loco) =>
            (float)Math.Round(Dvmp.Lib.Trains.Find(loco).SimController.controlsOverrider.Throttle.Value, 3);

        /// <summary>"throttle=x" once the loco's throttle is within 0.05 of <paramref name="expected"/>, or after <paramref name="seconds"/>.</summary>
        [Step]
        public static IEnumerator ThrottleWhen(Step<string> step, string loco, float expected, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            do
            {
                float value = Throttle(loco);
                bool near = Mathf.Abs(value - expected) < 0.05f;
                step.Result = $"throttle={(near ? expected : value):0.##}";
                if (near)
                    yield break;
                yield return new WaitForSecondsRealtime(0.25f);
            } while (Time.realtimeSinceStartup < end);
        }

        /// <summary>The loco's throttle once it is above <paramref name="above"/> and has settled, or after <paramref name="seconds"/>.</summary>
        [Step]
        public static IEnumerator ThrottleAbove(Step<float> step, string loco, float above, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Throttle(loco) <= above && Time.realtimeSinceStartup < end)
                yield return new WaitForSecondsRealtime(0.25f);
            yield return new WaitForSecondsRealtime(1.5f);   // the rest of the pushes
            step.Result = Throttle(loco);
        }

        /// <summary>Whether the loco's throttle is blocked in this game (a DPU receiver's are).</summary>
        [Step]
        public static bool ThrottleBlocked(string loco) =>
            Dvmp.Lib.Trains.Find(loco).SimController.controlsOverrider.Throttle.IsControlBlocked;

        /// <summary>Client: pushes the panel's throttle joystick forward <paramref name="times"/> times, as the
        /// joystick's driver reports each push.</summary>
        [Step]
        public static IEnumerator PushThrottle(ushort netId, int times)
        {
            var lod = Gadget(netId).LODObjects.OfType<GadgetControlPanelLODObject>().First();
            for (int i = 0; i < times; i++)
            {
                lod.UpdateOverridableControlUsingJoystick(1f, InteriorControlsManager.ControlType.Throttle);
                yield return new WaitForSecondsRealtime(0.3f);
            }
        }

        /// <summary>Host (player host): its player takes or gives back the loco's throttle lever.</summary>
        [Step]
        public static void HostHoldsThrottle(string loco, bool take)
        {
            TrainCar car = Dvmp.Lib.Trains.Find(loco);
            NetworkedTrainCar.TryGetNetId(car, out ushort carNetId);
            TrainRoles.Client.SendTrainControlAuthorityRequest(carNetId,
                NetworkedTrainCar.GetPortNetId(car.SimController.controlsOverrider.Throttle.portId), take);
        }

        /// <summary>One character per fault step from <paramref name="from"/>: '1' where the panel faults
        /// with <paramref name="chance"/>, as this game rolls it.</summary>
        [Step]
        public static string Faults(ushort netId, long from, int count, float chance)
        {
            var panel = (GadgetControlPanel)Gadget(netId);
            return new string(Enumerable.Range(0, count).Select(i => GadgetControlPanels.Faults(panel, from + i, chance) ? '1' : '0').ToArray());
        }

        [Step]
        public static float Rain(ushort netId) => GadgetControlPanels.Rain((GadgetControlPanel)Gadget(netId));
    }
}
