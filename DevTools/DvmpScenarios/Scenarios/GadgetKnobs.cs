using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using Multiplayer.Client.Gadgets;
using Multiplayer.Core.Gadgets;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Gadget knobs take leases like cab levers, and the server checks a setting's range (gadgets.md,
    /// PR 6c). The server puts a switch wired to a light and an amp limiter on a car (as
    /// gadget-settings does) and the client rejoins and rides the car.
    /// - The client grabs the switch's knob: the server records the client as holding it, and turning
    ///   the knob reaches the server's switch. Letting go frees it.
    /// - On a player host, the host's player grabs the knob: the client's knob is blocked and its change
    ///   to the switch from the HUD (no grab) is refused and put back; once the host's player lets go,
    ///   the client's knob is free again.
    /// - The client asks for an amp limiter mode its knob can't reach, and half a step: the server
    ///   refuses both and nothing changes in either game.
    /// </summary>
    [Scenario("gadget-knobs", "Gadget knobs take leases like cab levers: the holder's turns reach the server, others are blocked; out-of-range settings are refused")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class GadgetKnobs : Scenario<GadgetKnobs.Options>
    {
        public sealed class Options
        {
            /// <summary>The car the gadgets go on (default: the uncoupled car nearest the client).</summary>
            public string Car = "";
            /// <summary>How long a change may take to reach the other game, in seconds.</summary>
            public float Seconds = 10f;
            /// <summary>Delay on what the client receives once it has joined, in ms.</summary>
            public int Lag;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string car = string.IsNullOrEmpty(o.Car) ? await pair.Host.Run<string>(() => GadgetJoin.InGame.NearestUncoupledCar()) : o.Car;
            var built = await pair.Host.Run<GadgetSettingChanges.Built>(s => GadgetSettingChanges.InGame.Build(s, car));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(built.PersistentIds)));
            Report.Note(built.Note);
            Require.That(built.Switch != 0, "the server has a switch on the car", built.Note);

            await pair.Client.Quit();
            await Task.Delay(4000);   // the host notices the departure
            await pair.Rejoin();
            string host = await pair.Host.Run<string>(() => Gadgets.Digest(car));
            Expect.Equal(host, await pair.Client.Run<string>(s => Gadgets.DigestWhenEqual(s, car, host, 20f)),
                "after joining: the client's gadgets on the car, against the server's");
            await pair.Client.Run(() => Players.Board(car));
            await Task.Delay(2000);
            Require.That(await pair.Client.Run<int>(s => InGame.KnobsWhenLoaded(s, built.Switch, 10f)) > 0,
                "the client's switch has its knob loaded (it rides the car)");

            using (await pair.Client.Lag(o.Lag))
                await Checks(pair, o, car, built);
        }

        private static async Task Checks(Pair pair, Options o, string car, GadgetSettingChanges.Built built)
        {
            byte client = await pair.Client.Run<byte>(() => InGame.LocalPlayerId());

            // The client's grab: the lease is theirs, and their turn reaches the server.
            await pair.Client.Run(() => InGame.Grab(built.Switch, true));
            Expect.Equal(client, await pair.Host.Run<byte>(s => InGame.HolderWhen(s, built.Switch, client, o.Seconds)),
                "the client grabbed the switch's knob: who the server says holds it");
            await pair.Client.Run(() => InGame.TurnKnob(built.Switch, 1f));
            Expect.Equal("value=1", await pair.Client.Run<string>(() => GadgetSettingChanges.InGame.Setting(built.Switch)),
                "the client turned the knob it holds: its own switch, at once");
            Expect.Equal("value=1", await pair.Host.Run<string>(s => GadgetSettingChanges.InGame.SettingWhen(s, built.Switch, "value", 1f, o.Seconds)),
                "the client turned the knob it holds: the server's switch");
            await pair.Client.Run(() => InGame.Grab(built.Switch, false));
            Expect.Equal((byte)0, await pair.Host.Run<byte>(s => InGame.HolderWhen(s, built.Switch, 0, o.Seconds)),
                "the client let go of the knob: who the server says holds it");

            if (!pair.Dedicated)
            {
                // The host's player takes the knob: the client's is blocked, and its HUD change refused.
                // A lease is only granted at the car, as for a cab lever.
                await pair.Host.Run(() => Players.Board(car));
                await pair.Host.Run(() => InGame.HostTakes(built.Switch, true));
                Expect.Equal(true, await pair.Client.Run<bool>(s => InGame.BlockedWhen(s, built.Switch, true, o.Seconds)),
                    "the host's player holds the switch's knob: the client's knob is blocked");
                await pair.Client.Run(() => InGame.Hud(built.Switch, 0f));
                Expect.Equal("value=1", await pair.Client.Run<string>(s => GadgetSettingChanges.InGame.SettingWhen(s, built.Switch, "value", 1f, o.Seconds)),
                    "the client turned the switch off from the HUD while the host's player holds it: its switch, put back");
                Expect.Equal("value=1", await pair.Host.Run<string>(() => GadgetSettingChanges.InGame.Setting(built.Switch)),
                    "the client turned the switch off from the HUD while the host's player holds it: the server's switch");
                await pair.Host.Run(() => InGame.HostTakes(built.Switch, false));
                Expect.Equal(false, await pair.Client.Run<bool>(s => InGame.BlockedWhen(s, built.Switch, false, o.Seconds)),
                    "the host's player let go: the client's knob");
            }

            // The HUD path with nobody holding: a change without a grab goes through.
            await pair.Client.Run(() => InGame.Hud(built.Switch, 0f));
            Expect.Equal("value=0", await pair.Host.Run<string>(s => GadgetSettingChanges.InGame.SettingWhen(s, built.Switch, "value", 0f, o.Seconds)),
                "the client turned the switch off from the HUD, nobody holding it: the server's switch");

            // Out of range: refused, nothing changes.
            if (built.AmpLimiter != 0)
            {
                string before = await pair.Host.Run<string>(() => GadgetSettingChanges.InGame.Setting(built.AmpLimiter));
                await pair.Client.Run(() => InGame.Forge(built.AmpLimiter, "mode", 99f));
                await pair.Client.Run(() => InGame.Forge(built.AmpLimiter, "mode", 2.5f));
                await Task.Delay(3000);
                Expect.Equal(before, await pair.Host.Run<string>(() => GadgetSettingChanges.InGame.Setting(built.AmpLimiter)),
                    "the client asked for amp limiter modes 99 and 2.5: the server's amp limiter");
                Expect.Equal(before, await pair.Client.Run<string>(() => GadgetSettingChanges.InGame.Setting(built.AmpLimiter)),
                    "the client asked for amp limiter modes 99 and 2.5: the client's amp limiter");
            }
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static GadgetBase Gadget(ushort netId) =>
                NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null
                    ? item.GetComponent<GadgetItem>()?.Gadget
                    : null;

            private static ControlImplBase[] Knobs(GadgetBase gadget) =>
                gadget == null ? new ControlImplBase[0]
                    : gadget.LODObjects.Where(l => l != null).SelectMany(l => l.GetComponentsInChildren<ControlImplBase>(true)).ToArray();

            [Step]
            public static byte LocalPlayerId() => NetworkRole.LocalPlayerId;

            /// <summary>How many controls the gadget's loaded LOD has, once it has any, or after <paramref name="seconds"/>.</summary>
            [Step]
            public static IEnumerator KnobsWhenLoaded(Step<int> step, ushort netId, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    GadgetBase gadget = Gadget(netId);
                    step.Result = gadget != null && gadget.IsLODLoaded ? Knobs(gadget).Length : 0;
                    if (step.Result > 0)
                    {
                        yield return null;   // GadgetKnobLeases hooks a frame after the LOD loads
                        yield break;
                    }
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }

            /// <summary>Grabs or lets go of the gadget's first knob, as a hand on it would.</summary>
            [Step]
            public static void Grab(ushort netId, bool grab)
            {
                ControlImplBase knob = Knobs(Gadget(netId)).First();
                if (grab) knob.FireGrabbed();
                else knob.FireUngrabbed();
            }

            /// <summary>Turns the gadget's first knob to <paramref name="value"/>, as a hand on it would.</summary>
            [Step]
            public static void TurnKnob(ushort netId, float value) =>
                Knobs(Gadget(netId)).First().SetValue(value);

            /// <summary>The HUD's change: vanilla's setter, then the knobs moved to match, no grab.</summary>
            [Step]
            public static void Hud(ushort netId, float value) =>
                GadgetSettings.Apply(Gadget(netId), new[] { "value" }, new[] { value });

            /// <summary>Host: the player id holding the gadget's knobs once it is <paramref name="expected"/>, or after <paramref name="seconds"/>; 0 for nobody.</summary>
            [Step]
            public static IEnumerator HolderWhen(Step<byte> step, ushort netId, byte expected, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    step.Result = Multiplayer.Server.Gadgets.GadgetStateServer.KnobHolder(Gadget(netId), netId)?.PlayerId ?? 0;
                    if (step.Result == expected)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }

            /// <summary>Host (player host): its player takes or gives back the gadget's knobs, as a grab does.</summary>
            [Step]
            public static void HostTakes(ushort netId, bool take)
            {
                GadgetBase gadget = Gadget(netId);
                TrainCar car = ((TrainCarCustomization)gadget.Custom).TrainCar;
                NetworkedTrainCar.TryGetNetId(car, out ushort carNetId);
                TrainRoles.Client.SendTrainControlAuthorityRequest(carNetId, GadgetKnobLease.IdOf(netId), take);
            }

            /// <summary>Whether this game shows the gadget's knobs as another player's, once that is
            /// <paramref name="expected"/> or after <paramref name="seconds"/>: the lease, and the knob not
            /// taking a hand.</summary>
            [Step]
            public static IEnumerator BlockedWhen(Step<bool> step, ushort netId, bool expected, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    GadgetBase gadget = Gadget(netId);
                    step.Result = GadgetKnobLeases.HeldByAnother(gadget) && Knobs(gadget).All(k => !k.InteractionAllowed);
                    if (step.Result == expected)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }

            /// <summary>Sends the server a setting request with a value the knob can't reach.</summary>
            [Step, ClientOnly]
            public static void Forge(ushort netId, string key, float value) =>
                Multiplayer.Client.Session.NetworkClient.Instance.SendGadgetSetting(new ServerboundGadgetSettingPacket
                {
                    ItemNetId = netId,
                    OperationId = 900000 + (uint)Mathf.Abs(value * 10f),
                    Keys = new[] { key },
                    Values = new[] { value }
                });
        }
    }
}
