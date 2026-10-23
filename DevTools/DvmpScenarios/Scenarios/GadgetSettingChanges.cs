using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A setting changed on an installed gadget reaches every game (gadgets.md, PR 6a). The server puts
    /// a switch wired to a light and an amp limiter on a car, and the client joins again to get them.
    /// The client, riding the car, turns the switch on: the server's switch and light follow. The
    /// server turns the amp limiter to another mode: the client's follows. The client, walked away from
    /// the car, turns the switch off: the server refuses it, and the client's switch goes back on.
    /// </summary>
    [Scenario("gadget-settings", "A setting changed on an installed gadget reaches every game; one changed out of reach is put back")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class GadgetSettingChanges : Scenario<GadgetSettingChanges.Options>
    {
        public sealed class Options
        {
            /// <summary>The car the gadgets go on (default: the uncoupled car nearest the client).</summary>
            public string Car = "";
            /// <summary>How long a change may take to reach the other game, in seconds.</summary>
            public float Seconds = 10f;
            /// <summary>Delay on what the client receives once it has joined, in ms (a far player).</summary>
            public int Lag;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string car = string.IsNullOrEmpty(o.Car) ? await pair.Host.Run<string>(() => GadgetJoin.InGame.NearestUncoupledCar()) : o.Car;
            Built built = await pair.Host.Run<Built>(s => InGame.Build(s, car));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(built.PersistentIds)));
            Report.Note(built.Note);
            Require.That(built.Switch != 0, "the server has a switch on the car", built.Note);

            await pair.Client.Quit();
            await Task.Delay(4000);   // the host notices the departure
            await pair.Rejoin();
            string host = await pair.Host.Run<string>(() => Gadgets.Digest(car));
            Expect.Equal(host, await pair.Client.Run<string>(s => Gadgets.DigestWhenEqual(s, car, host, 20f)),
                "after joining: the client's gadgets on the car, against the server's");
            using (await pair.Client.Lag(o.Lag))
                await Changes(pair, o, car, built);
        }

        private static async Task Changes(Pair pair, Options o, string car, Built built)
        {
            // The client at the switch, riding the car.
            await pair.Client.Run(() => Players.Board(car));
            await Task.Delay(2000);
            await pair.Client.Run(() => InGame.Set(built.Switch, "value", 1f));
            string client = await pair.Client.Run<string>(s => InGame.SettingWhen(s, built.Switch, "value", 1f, 2f));
            Expect.Equal("value=1", client, "the client's switch shows its own change at once");
            await Task.Delay(1000);
            string clientCar = await pair.Client.Run<string>(() => Gadgets.Digest(car));
            Expect.Equal(clientCar, await pair.Host.Run<string>(s => Gadgets.DigestWhenEqual(s, car, clientCar, o.Seconds)),
                "the client turned the switch on: the server's car (switch, light's power), against the client's");

            // The server's change: a player-hosted host's player at the knob, or the server's own.
            if (built.AmpLimiter != 0)
            {
                float mode = await pair.Host.Run<float>(() => InGame.NextAmpLimiterMode(built.AmpLimiter));
                Expect.Equal($"mode={mode:0.###}",
                    await pair.Client.Run<string>(s => InGame.SettingWhen(s, built.AmpLimiter, "mode", mode, o.Seconds)),
                    "the server turned the amp limiter: the client's amp limiter's mode");
            }

            // Out of reach: the change shows, the server refuses it, and the client's switch goes back.
            float[] carAt = await pair.Host.Run<float[]>(() => InGame.CarPosition(car));
            await pair.Client.Run(() => Players.TeleportToGround(carAt[0] + 40f, carAt[1]), 90);
            await Task.Delay(2000);
            await pair.Client.Run(() => InGame.Set(built.Switch, "value", 0f));
            Expect.Equal("value=1", await pair.Client.Run<string>(s => InGame.SettingWhen(s, built.Switch, "value", 1f, o.Seconds)),
                "the client turned the switch off from 40 m away: its switch, put back by the server");
            Expect.Equal("value=1", await pair.Host.Run<string>(() => InGame.Setting(built.Switch)),
                "the client turned the switch off from 40 m away: the server's switch");
        }

        public sealed class Built
        {
            public ushort Switch;
            public ushort AmpLimiter;
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

            /// <summary>Host: a switch at 0 wired to a light, and an amp limiter, on <paramref name="carId"/>.</summary>
            [Step]
            public static IEnumerator Build(Step<Built> step, string carId)
            {
                var client = Gadgets.Client();
                TrainCar car = CarSpawner.Instance.AllCars.First(c => c != null && c.ID == carId);
                Vector3 beside = client.WorldPosition + Vector3.right * 1.5f + Vector3.up;
                var items = new List<NetworkedItem>();
                Vector3 at(float x, float z) => car.interior.TransformPoint(new Vector3(x, 1.5f, z));

                InventoryItemSpec Spec(Func<GadgetBase, bool> kind) => DV.Globals.G.Items.items
                    .Where(spec => spec != null && spec.GetComponent<GadgetItem>()?.gadgetPrefab is { } g && kind(g))
                    .OrderBy(spec => spec.ItemPrefabName, StringComparer.Ordinal).FirstOrDefault();

                IEnumerator Install(InventoryItemSpec spec, Vector3 where, Action<GadgetBase> got)
                {
                    NetworkedItem item = null;
                    yield return Gadgets.SpawnOwned(spec, beside, client, i => item = i);
                    items.Add(item);
                    got(Gadgets.Install(item, car.Customization, where));
                }

                GadgetBase sw = null, light = null, amp = null;
                var notes = new List<string>();
                InventoryItemSpec switchSpec = Spec(g => g is GadgetSwitch && !(g is AlternatingController));
                InventoryItemSpec lightSpec = Spec(g => g is GadgetLight);
                InventoryItemSpec ampSpec = Spec(g => g is GadgetAmpLimiter);
                if (switchSpec != null)
                {
                    yield return Install(switchSpec, at(0.3f, 0f), g => sw = g);
                    ((GadgetSwitch)sw).SetOutputValue(0f);
                }
                if (sw != null && lightSpec != null)
                {
                    yield return Install(lightSpec, at(0.6f, 0f), g => light = g);
                    if (sw.wiring.TryGetCompatiblePorts(light.wiring, out var a, out var b))
                        GadgetWiringModule.WireLinkPort.Wire(a, b);
                    else
                        notes.Add("the switch and the light have no compatible ports: not wired");
                }
                if (ampSpec != null)
                    yield return Install(ampSpec, at(0f, 0.4f), g => amp = g);
                else
                    notes.Add("no amp limiter in this game: the server's change left out");
                for (int i = 0; i < 10; i++) yield return null;

                ushort NetId(GadgetBase g) => g != null && g.IsLinked ? g.GadgetItem.GetComponent<NetworkedItem>().NetId : (ushort)0;
                step.Result = new Built
                {
                    Switch = NetId(sw),
                    AmpLimiter = NetId(amp),
                    PersistentIds = items.Select(i => i.GetComponent<Multiplayer.Server.Items.SaveLoad.PersistentWorldItemIdentity>().Value.ToString("D")).ToArray(),
                    Note = string.Join("; ", notes.Prepend($"built on {carId}: {string.Join(", ", new[] { sw, light, amp }.Where(g => g != null).Select(g => g.name))}"))
                };
            }

            /// <summary>Sets one of the gadget's settings through vanilla's setter, as a player at it would.</summary>
            [Step]
            public static void Set(ushort netId, string key, float value) =>
                Multiplayer.Shared.Gadgets.GadgetSettings.Apply(Gadget(netId), new[] { key }, new[] { value });

            /// <summary>The gadget's settings in this game, "key=value" each, or "(none)".</summary>
            [Step]
            public static string Setting(ushort netId)
            {
                GadgetBase gadget = Gadget(netId);
                var settings = gadget == null ? null : Multiplayer.Shared.Gadgets.GadgetSettings.Capture(gadget);
                return settings == null
                    ? "(none)"
                    : string.Join(" ", settings.Select(s => $"{s.Key}={s.Value:0.###}"));
            }

            /// <summary>"key=value" for one setting once it reads <paramref name="expected"/>, or after <paramref name="seconds"/>.</summary>
            [Step]
            public static IEnumerator SettingWhen(Step<string> step, ushort netId, string key, float expected, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    GadgetBase gadget = Gadget(netId);
                    var settings = gadget == null ? null : Multiplayer.Shared.Gadgets.GadgetSettings.Capture(gadget);
                    float value = 0f;
                    bool has = settings != null && settings.TryGetValue(key, out value);
                    step.Result = has ? $"{key}={value:0.###}" : $"(no {key})";
                    if (has && Mathf.Abs(value - expected) < 1e-3f)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }

            /// <summary>Host: turns the amp limiter to its next mode and returns it.</summary>
            [Step]
            public static float NextAmpLimiterMode(ushort netId)
            {
                var amp = (GadgetAmpLimiter)Gadget(netId);
                amp.ModeIndex = (amp.ModeIndex + 1) % amp.ModeCount;
                return amp.ModeIndex;
            }

            /// <summary>Host: the car's absolute x and z (as the teleport steps take them).</summary>
            [Step]
            public static float[] CarPosition(string carId)
            {
                Vector3 at = Dev.Abs(CarSpawner.Instance.AllCars.First(c => c != null && c.ID == carId).transform.position);
                return new[] { at.x, at.z };
            }
        }
    }
}
