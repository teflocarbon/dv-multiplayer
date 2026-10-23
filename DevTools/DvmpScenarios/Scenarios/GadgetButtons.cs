using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// What installed gadgets do on their own, and their buttons, are the same in every game
    /// (gadgets.md, PR 6b). The server puts a distance tracker, an ATS, a defect detector, an
    /// alternating controller wired to two lights and a beacon wired to a switch on a loco it spawns
    /// by the client, all soldered, with the loco's electronics on; the client joins again to get them.
    /// - The client presses the distance tracker's start: the server's starts counting. It presses
    ///   acknowledge: the server's stops.
    /// - The server's ATS interval is turned: the client's countdown shows the new interval.
    /// - The loco derails on the server, and the client presses the defect detector's snooze: the
    ///   server's detector is snoozed.
    /// - The alternating controller runs on the server: the client's phase follows it.
    /// - The beacon turns at the same angle in both games (within <see cref="Options.Degrees"/>).
    /// </summary>
    [Scenario("gadget-buttons", "Installed gadgets' buttons and running state are the same in every game: distance tracker, ATS, defect detector snooze, alternating controller, beacon")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class GadgetButtons : Scenario<GadgetButtons.Options>
    {
        public sealed class Options
        {
            public string Livery = "LocoDE2";
            /// <summary>How long a change may take to reach the other game, in seconds.</summary>
            public float Seconds = 10f;
            /// <summary>How far apart the two games' beacon angles may be: the tick clock is shared to
            /// within a tick or two, and a beacon turns 15° a tick.</summary>
            public float Degrees = 45f;
            /// <summary>Delay on what the client receives once it has joined, in ms.</summary>
            public int Lag;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Loco loco = await pair.Host.Run<Loco>(s => Cars.SpawnLocoNear(s, o.Livery, true, "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            Built built = await pair.Host.Run<Built>(s => InGame.Build(s, loco.Id));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(built.PersistentIds)));
            Report.Note(built.Note);
            Report.Note("host power: " + await pair.Host.Run<string>(s => InGame.Power(s, loco.Id)));

            await pair.Client.Quit();
            await Task.Delay(4000);   // the host notices the departure
            await pair.Rejoin();
            string host = await pair.Host.Run<string>(() => Gadgets.Digest(loco.Id));
            Expect.Equal(host, await pair.Client.Run<string>(s => Gadgets.DigestWhenEqual(s, loco.Id, host, 20f)),
                "after joining: the client's gadgets on the loco, against the server's");
            Report.Note("client power: " + await pair.Client.Run<string>(s => InGame.Power(s, loco.Id)));
            await pair.Client.Run(() => Players.Board(loco.Id));
            await Task.Delay(2000);

            using (await pair.Client.Lag(o.Lag))
                await Checks(pair, o, loco.Id, built);
        }

        private static async Task Checks(Pair pair, Options o, string loco, Built built)
        {
            if (built.Roadrunner != 0)
            {
                await pair.Client.Run(() => InGame.Press(built.Roadrunner, "start"));
                Expect.Equal("countup>=0", await pair.Host.Run<string>(s => InGame.CountingWhen(s, built.Roadrunner, true, o.Seconds)),
                    "the client pressed the distance tracker's start: the server's tracker");
                await pair.Client.Run(() => InGame.Press(built.Roadrunner, "acknowledge"));
                Expect.Equal("countup<0", await pair.Host.Run<string>(s => InGame.CountingWhen(s, built.Roadrunner, false, o.Seconds)),
                    "the client pressed the distance tracker's acknowledge: the server's tracker");
            }

            if (built.Ats != 0)
            {
                await pair.Host.Run(() => InGame.SetSetting(built.Ats, "mode", 2f));
                float length = await pair.Host.Run<float>(() => InGame.AtsLength(built.Ats));
                Expect.Equal($"timer={length:0}", await pair.Client.Run<string>(s => InGame.RunningWhen(s, built.Ats, "timer", length, 1f, o.Seconds)),
                    "the server turned the ATS interval: the client's countdown");
            }

            if (built.Detector != 0)
            {
                Expect.That(await pair.Host.Run<bool>(() => Cars.Derail(loco)), "the loco is derailed on the server");
                await Task.Delay(3000);   // the derailment reaches the client
                await pair.Client.Run(() => InGame.Press(built.Detector, "snooze"));
                Expect.Equal("snoozed=1", await pair.Host.Run<string>(s => InGame.RunningWhen(s, built.Detector, "snoozed", 1f, 0f, o.Seconds)),
                    "the client snoozed the defect detector: the server's detector");
                Expect.Equal("snoozed=1", await pair.Client.Run<string>(s => InGame.RunningWhen(s, built.Detector, "snoozed", 1f, 0f, 2f)),
                    "the client snoozed the defect detector: its own");
            }

            if (built.Alternator != 0)
            {
                await pair.Host.Run(() => InGame.SetSetting(built.Alternator, "value", 2f));   // every 0.5 s
                int changes = await pair.Client.Run<int>(s => InGame.PhaseChanges(s, built.Alternator, 4f));
                Expect.That(changes >= 4, "the alternating controller runs on the server: the client's phase changes (every 0.5 s)", $"{changes} changes in 4 s");
            }

            if (built.Beacon != 0)
            {
                float hostOffset = await pair.Host.Run<float>(() => InGame.BeaconOffset(built.Beacon));
                float clientOffset = await pair.Client.Run<float>(() => InGame.BeaconOffset(built.Beacon));
                Report.Note($"beacon angle against the session clock: host {hostOffset:0.0}°, client {clientOffset:0.0}°");
                Expect.That(hostOffset >= 0 && clientOffset >= 0, "the beacon turns in both games", $"host {hostOffset:0.0}, client {clientOffset:0.0}");
                float apart = Math.Abs(((clientOffset - hostOffset) % 360f + 540f) % 360f - 180f);
                Expect.That(apart <= o.Degrees, "the beacon's angle, the same in both games", $"{apart:0.0}° apart");
            }
        }

        public sealed class Built
        {
            public ushort Roadrunner, Ats, Detector, Alternator, Beacon;
            public string[] PersistentIds;
            public string Note;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static GadgetBase Gadget(ushort netId) =>
                NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null
                    ? item.GetComponent<GadgetItem>()?.Gadget
                    : null;

            /// <summary>Host: the gadgets on the loco (see the scenario), soldered and wired.</summary>
            [Step]
            public static IEnumerator Build(Step<Built> step, string carId)
            {
                var client = Gadgets.Client();
                TrainCar car = CarSpawner.Instance.AllCars.First(c => c != null && c.ID == carId);
                Vector3 beside = client.WorldPosition + Vector3.right * 1.5f + Vector3.up;
                var items = new List<NetworkedItem>();
                var notes = new List<string>();
                Vector3 at(float x, float z) => car.interior.TransformPoint(new Vector3(x, 1.5f, z));

                InventoryItemSpec Spec(Func<GadgetBase, bool> kind) => DV.Globals.G.Items.items
                    .Where(spec => spec != null && spec.GetComponent<GadgetItem>()?.gadgetPrefab is { } g && kind(g))
                    .OrderBy(spec => spec.ItemPrefabName, StringComparer.Ordinal).FirstOrDefault();

                IEnumerator Install(Func<GadgetBase, bool> kind, string what, Vector3 where, Action<GadgetBase> got)
                {
                    InventoryItemSpec spec = Spec(kind);
                    if (spec == null)
                    {
                        notes.Add($"no {what} in this game");
                        yield break;
                    }
                    NetworkedItem item = null;
                    yield return Gadgets.SpawnOwned(spec, beside, client, i => item = i);
                    items.Add(item);
                    GadgetBase gadget = Gadgets.Install(item, car.Customization, where);
                    if (gadget is TrainCarCustomization.TrainCarCustomizerBase c && c.IsSolderable)
                        c.MakeSoldered();
                    got(gadget);
                }

                void Wire(GadgetBase from, GadgetBase to)
                {
                    if (from == null || to == null)
                        return;
                    if (from.wiring.TryGetCompatiblePorts(to.wiring, out var a, out var b))
                        GadgetWiringModule.WireLinkPort.Wire(a, b);
                    else
                        notes.Add($"{from.name} and {to.name} have no compatible ports: not wired");
                }

                GadgetBase roadrunner = null, ats = null, detector = null, alternator = null, lightA = null, lightB = null, beacon = null, sw = null;
                yield return Install(g => g is GadgetRoadrunner, "distance tracker", at(0f, 0f), g => roadrunner = g);
                yield return Install(g => g is GadgetATS, "ATS", at(0.3f, 0f), g => ats = g);
                yield return Install(g => g.GetComponentInChildren<GadgetDefectDetectorLOD>(true) != null, "defect detector", at(0.6f, 0f), g => detector = g);
                yield return Install(g => g is AlternatingController, "alternating controller", at(0f, 0.4f), g => alternator = g);
                yield return Install(g => g is GadgetLight, "light", at(0.3f, 0.4f), g => lightA = g);
                yield return Install(g => g is GadgetLight, "light", at(0.6f, 0.4f), g => lightB = g);
                yield return Install(g => g is GadgetBeacon, "beacon", at(0f, 0.8f), g => beacon = g);
                yield return Install(g => g is GadgetSwitch && !(g is AlternatingController), "switch", at(0.3f, 0.8f), g => sw = g);
                Wire(alternator, lightA);
                Wire(alternator, lightB);
                Wire(sw, beacon);
                (sw as GadgetSwitch)?.SetOutputValue(1f);
                for (int i = 0; i < 10; i++) yield return null;

                ushort NetId(GadgetBase g) => g != null && g.IsLinked ? g.GadgetItem.GetComponent<NetworkedItem>().NetId : (ushort)0;
                step.Result = new Built
                {
                    Roadrunner = NetId(roadrunner),
                    Ats = NetId(ats),
                    Detector = NetId(detector),
                    Alternator = NetId(alternator),
                    Beacon = NetId(beacon),
                    PersistentIds = items.Select(i => i.GetComponent<Multiplayer.Server.Items.SaveLoad.ItemIdentity>().Value.ToString("D")).ToArray(),
                    Note = string.Join("; ", notes.Prepend($"built on {carId}: {string.Join(", ", new[] { roadrunner, ats, detector, alternator, lightA, lightB, beacon, sw }.Where(g => g != null).Select(g => g.name))}"))
                };
            }

            /// <summary>Turns the loco's electronics on in this game, and reports each gadget's power.</summary>
            [Step]
            public static IEnumerator Power(Step<string> step, string carId)
            {
                TrainCar car = CarSpawner.Instance.AllCars.First(c => c != null && c.ID == carId);
                var fuse = car.Customization?.electronicsFuse;
                fuse?.ChangeState(true);
                for (int i = 0; i < 30; i++) yield return null;
                step.Result = $"fuse {(fuse == null ? "none" : fuse.State.ToString())}: " + string.Join(", ",
                    car.Customization.Customizers.OfType<GadgetBase>().Select(g => $"{g.name}={g.PowerState}"));
            }

            /// <summary>Presses a gadget's button as a player at it would, through vanilla's button method:
            /// "ats", "start", "acknowledge" (the distance tracker's) or "snooze".</summary>
            [Step]
            public static void Press(ushort netId, string button)
            {
                GadgetBase gadget = Gadget(netId);
                switch (button)
                {
                    case "ats":
                        gadget.LODObjects.OfType<GadgetATSLOD>().First().OnAckButton();
                        break;
                    case "start":
                        ((GadgetRoadrunner)gadget).StartMeasure();
                        break;
                    case "acknowledge":
                        ((GadgetRoadrunner)gadget).Acknowledge();
                        break;
                    case "snooze":
                        GadgetRunningState.DefectDetector(gadget).Snooze();
                        break;
                    default:
                        throw new ArgumentException("no button " + button);
                }
            }

            [Step]
            public static void SetSetting(ushort netId, string key, float value) =>
                GadgetSettings.Apply(Gadget(netId), new[] { key }, new[] { value });

            [Step]
            public static float AtsLength(ushort netId) => ((GadgetATS)Gadget(netId)).CurrentRegimeTimerLength;

            /// <summary>"countup>=0" or "countup&lt;0" once the tracker is (or isn't) counting, or after <paramref name="seconds"/>.</summary>
            [Step]
            public static IEnumerator CountingWhen(Step<string> step, ushort netId, bool counting, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    var tracker = (GadgetRoadrunner)Gadget(netId);
                    step.Result = tracker.Countup >= 0f ? "countup>=0" : "countup<0";
                    if (tracker.Countup >= 0f == counting)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }

            /// <summary>"key=value" for a running key once it is within <paramref name="tolerance"/> of
            /// <paramref name="expected"/>, or after <paramref name="seconds"/>.</summary>
            [Step]
            public static IEnumerator RunningWhen(Step<string> step, ushort netId, string key, float expected, float tolerance, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    var state = GadgetRunningState.Capture(Gadget(netId));
                    float value = 0f;
                    bool has = state != null && state.TryGetValue(key, out value);
                    bool near = has && Mathf.Abs(value - expected) <= tolerance + 1e-3f;
                    step.Result = has ? $"{key}={(near ? expected : value):0.###}" : $"(no {key})";
                    if (near)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.1f);
                } while (Time.realtimeSinceStartup < end);
            }

            /// <summary>How many times the alternating controller's phase changes in this game in <paramref name="seconds"/>.</summary>
            [Step]
            public static IEnumerator PhaseChanges(Step<int> step, ushort netId, float seconds)
            {
                var controller = (AlternatingController)Gadget(netId);
                int last = controller.alternatorState, changes = 0;
                float end = Time.realtimeSinceStartup + seconds;
                while (Time.realtimeSinceStartup < end)
                {
                    if (controller.alternatorState != last)
                    {
                        changes++;
                        last = controller.alternatorState;
                    }
                    yield return null;
                }
                step.Result = changes;
            }

            /// <summary>The beacon's angle less what it has turned by the session clock, in degrees
            /// (the same in every game if they show the same angle); -1 if it isn't turning.</summary>
            [Step]
            public static float BeaconOffset(ushort netId)
            {
                var beacon = (GadgetBeacon)Gadget(netId);
                if (beacon.rotateSpeed == 0f)
                    return -1f;
                double turned = beacon.rotateSpeed * Multiplayer.Shared.Gadgets.GadgetSharedMotionPatches.SessionSeconds();
                return (float)(((beacon.head.localEulerAngles.y - turned) % 360.0 + 360.0) % 360.0);
            }
        }
    }
}
