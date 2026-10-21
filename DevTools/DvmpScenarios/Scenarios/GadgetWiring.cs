using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using Multiplayer.Shared.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Wiring and soldering go through the server (gadgets.md, PR 5). The server puts a switch (on) and a
    /// light on a powered DE2 by the client, neither wired nor soldered, and a crimping tool and a
    /// soldering iron by the client; the client rides the DE2.
    /// - With the crimping tool it wires the switch to the light: wired on the server and in its game.
    ///   It unwires them, and wires them again.
    /// - With the iron it solders both: its own game shows them soldered at once, the server's follow,
    ///   and the light is powered in both games.
    /// </summary>
    [Scenario("gadget-wiring", "Wiring, unwiring and soldering go through the server; a soldered, wired light is powered in every game")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class GadgetWiring : Scenario<GadgetWiring.Options>
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
            Kit kit = await pair.Host.Run<Kit>(s => InGame.Build(s, loco.Id));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(kit.PersistentIds)));
            Report.Note(kit.Note);
            Require.That(kit.Switch != 0 && kit.Light != 0 && kit.Crimper != 0, "the server put a switch, a light and a crimping tool out", kit.Note);
            Report.Note("host power: " + await pair.Host.Run<string>(s => GadgetButtons.InGame.Power(s, loco.Id)));
            await Digests(pair, o, loco.Id, "the gadgets reached the client");

            using (await pair.Client.Lag(o.Lag))
            {
                await Hold(pair, kit.Crimper, loco.Id);
                await pair.Client.Run(() => InGame.Wire(kit.Switch, kit.Light, true));
                Expect.Equal(true, await pair.Host.Run<bool>(s => InGame.WiredWhen(s, kit.Switch, kit.Light, true, o.Seconds)),
                    "the client wired the switch to the light: wired on the server");
                await Digests(pair, o, loco.Id, "the client wired the switch to the light");
                await pair.Client.Run(() => InGame.Wire(kit.Switch, kit.Light, false));
                Expect.Equal(false, await pair.Host.Run<bool>(s => InGame.WiredWhen(s, kit.Switch, kit.Light, false, o.Seconds)),
                    "the client unwired them: not wired on the server");
                await Digests(pair, o, loco.Id, "the client unwired them");
                await pair.Client.Run(() => InGame.Wire(kit.Switch, kit.Light, true));
                Expect.Equal(true, await pair.Host.Run<bool>(s => InGame.WiredWhen(s, kit.Switch, kit.Light, true, o.Seconds)),
                    "the client wired them again: wired on the server");
                await pair.Client.Run(() => Items.Drop());

                if (kit.Iron != 0)
                {
                    await Hold(pair, kit.Iron, loco.Id);
                    foreach (ushort gadget in new[] { kit.Switch, kit.Light })
                    {
                        int own = await pair.Client.Run<int>(s => InGame.Solder(s, gadget, 4f), 30);
                        Expect.Equal(65536, own, "the client soldered a gadget: its own game, at once");
                        Expect.Equal(65536, await pair.Host.Run<int>(s => InGame.UnitsWhen(s, gadget, 65536, o.Seconds)),
                            "the client soldered a gadget: the server's units");
                    }
                    await Digests(pair, o, loco.Id, "the client soldered both (the light powered through the switch)");
                    Report.Note("light powered: server " + await pair.Host.Run<bool>(() => InGame.Powered(kit.Light)) +
                                ", client " + await pair.Client.Run<bool>(() => InGame.Powered(kit.Light)));
                }
            }
        }

        private static async Task Hold(Pair pair, ushort tool, string loco)
        {
            await pair.Client.Run(() => Items.Pickup(tool), 60);
            Require.Equal(tool, await pair.Client.Run<ushort>(() => Items.Held()), "the client holds the tool");
            await pair.Client.Run(() => Players.Board(loco));
            await Task.Delay(1500);
        }

        private static async Task Digests(Pair pair, Options o, string loco, string what)
        {
            string host = await pair.Host.Run<string>(() => Gadgets.Digest(loco));
            Expect.Equal(host, await pair.Client.Run<string>(s => Gadgets.DigestWhenEqual(s, loco, host, o.Seconds + 10f)),
                $"{what}: the client's gadgets on the loco, against the server's");
        }

        public sealed class Kit
        {
            public ushort Switch, Light, Crimper, Iron;
            public string[] PersistentIds;
            public string Note;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static GadgetBase Gadget(ushort netId) =>
                NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null ? item.GetComponent<GadgetItem>()?.Gadget : null;

            private static InventoryItemSpec Spec(Func<InventoryItemSpec, bool> fits) =>
                DV.Globals.G.Items.items.Where(s => s != null && fits(s)).OrderBy(s => s.ItemPrefabName, StringComparer.Ordinal).FirstOrDefault();

            /// <summary>Host: a switch (on) and a light on the loco, unwired and unsoldered, and a crimping
            /// tool and a soldering iron by the client, all owned by it.</summary>
            [Step]
            public static IEnumerator Build(Step<Kit> step, string carId)
            {
                var client = Gadgets.Client();
                TrainCar car = Dvmp.Lib.Trains.Find(carId);
                Vector3 beside = client.WorldPosition + Vector3.right * 0.8f + Vector3.up * 0.6f;
                var items = new System.Collections.Generic.List<NetworkedItem>();
                IEnumerator Spawn(InventoryItemSpec spec, Vector3 at, Action<NetworkedItem> got)
                {
                    if (spec == null)
                        yield break;
                    NetworkedItem item = null;
                    yield return Gadgets.SpawnOwned(spec, at, client, i => item = i);
                    items.Add(item);
                    got(item);
                }

                NetworkedItem sw = null, light = null, crimper = null, iron = null;
                yield return Spawn(Spec(s => s.GetComponent<GadgetItem>()?.gadgetPrefab is GadgetSwitch g && !(g is AlternatingController)), beside, i => sw = i);
                yield return Spawn(Spec(s => s.GetComponent<GadgetItem>()?.gadgetPrefab is GadgetLight), beside, i => light = i);
                yield return Spawn(Spec(s => s.GetComponentInChildren<GadgetWiringTool>(true) != null), beside + Vector3.forward * 0.5f, i => crimper = i);
                yield return Spawn(Spec(s => s.GetComponentInChildren<GadgetSolderingTool>(true) != null), beside + Vector3.forward * 1f, i => iron = i);
                GadgetBase swGadget = sw != null ? Gadgets.Install(sw, car.Customization, car.interior.TransformPoint(new Vector3(0.3f, 1.5f, 0f))) : null;
                GadgetBase lightGadget = light != null ? Gadgets.Install(light, car.Customization, car.interior.TransformPoint(new Vector3(0.6f, 1.5f, 0f))) : null;
                (swGadget as GadgetSwitch)?.SetOutputValue(1f);
                for (int i = 0; i < 10; i++) yield return null;
                ushort Id(NetworkedItem i, GadgetBase g = null) => i != null && (g == null || g.IsLinked) ? i.NetId : (ushort)0;
                step.Result = new Kit
                {
                    Switch = Id(sw, swGadget),
                    Light = Id(light, lightGadget),
                    Crimper = Id(crimper),
                    Iron = Id(iron),
                    PersistentIds = items.Where(i => i != null)
                        .Select(i => i.GetComponent<Multiplayer.Server.Items.PersistentWorldItemIdentity>().Value.ToString("D")).ToArray(),
                    Note = $"switch {sw?.name}, light {light?.name}, crimper {crimper?.name}, iron {iron?.name}"
                };
            }

            /// <summary>Wires (or unwires) the two gadgets' compatible ports, as the crimping tool does.</summary>
            [Step]
            public static void Wire(ushort a, ushort b, bool wire)
            {
                if (!Gadget(a).wiring.TryGetCompatiblePorts(Gadget(b).wiring, out var pa, out var pb))
                    throw new InvalidOperationException("the two gadgets have no compatible ports");
                if (wire)
                    GadgetWiringModule.WireLinkPort.Wire(pa, pb);
                else
                    GadgetWiringModule.WireLinkPort.Unwire(pa, pb);
            }

            /// <summary>Whether the two are wired here, once it is <paramref name="expected"/> or after <paramref name="seconds"/>.</summary>
            [Step]
            public static IEnumerator WiredWhen(Step<bool> step, ushort a, ushort b, bool expected, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    step.Result = Gadget(a).wiring.TryGetCompatiblePorts(Gadget(b).wiring, out var pa, out var pb) &&
                                  GadgetWiringModule.WireLinkPort.AreWired(pa, pb);
                    if (step.Result == expected)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }

            /// <summary>Client: holds the soldering iron on the gadget for <paramref name="seconds"/>, as the
            /// iron's work animation does every frame (its spool filled first). Result: the units here.</summary>
            [Step]
            public static IEnumerator Solder(Step<int> step, ushort netId, float seconds)
            {
                var iron = Resources.FindObjectsOfTypeAll<GadgetSolderingTool>().First(t => t.gameObject.scene.IsValid() && t.gameObject.activeInHierarchy);
                GadgetBase gadget = Gadget(netId);
                iron.remainingUnits = 65536 * 4;
                for (float end = Time.realtimeSinceStartup + seconds; Time.realtimeSinceStartup < end;)
                {
                    iron.ProcessSoldering(gadget);
                    yield return null;
                }
                step.Result = 65536 - ((TrainCarCustomization.TrainCarCustomizerBase)gadget).SolderingUnitsRequired;
            }

            [Step]
            public static IEnumerator UnitsWhen(Step<int> step, ushort netId, int expected, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    step.Result = 65536 - ((TrainCarCustomization.TrainCarCustomizerBase)Gadget(netId)).SolderingUnitsRequired;
                    if (step.Result == expected)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }

            [Step]
            public static bool Powered(ushort netId) => Gadget(netId).PowerState;
        }
    }
}
