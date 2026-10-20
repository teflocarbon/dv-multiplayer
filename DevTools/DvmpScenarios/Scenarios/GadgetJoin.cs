using System;
using System.Collections;
using System.Collections.Generic;
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
    /// A player joining sees the gadgets installed on the trains as the server has them (gadgets.md,
    /// PR 2). The server builds a loco full of gadgets the way vanilla's tools leave them: a simple
    /// gadget; a switch at half and a light, wired together and soldered; a plate bolted at two points
    /// with a gadget mounted on it; and a hole left by a bolted plate taken off. Then the client quits
    /// and joins again. Its game must have each gadget once, inactive and installed, with the same UID,
    /// pose, power, soldering, wires, mount points, mount and settings as the server's, and the hole.
    /// </summary>
    [Scenario("gadget-join", "A joining player gets every gadget installed on the trains as the server has it: UIDs, wires, soldering, mounts, settings, power and holes")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class GadgetJoin : Scenario<GadgetJoin.Options>
    {
        public sealed class Options
        {
            /// <summary>The loco the gadgets go on.</summary>
            public string Loco = "LocoDE2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            HeldLoco loco = await pair.Host.Run<HeldLoco>(s => Cars.SpawnHeldLoco(s, o.Loco));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            Built built = await pair.Host.Run<Built>(s => InGame.Build(s, loco.Loco));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(built.PersistentIds)));
            foreach (string note in built.Notes)
                Report.Note(note);

            string host = await pair.Host.Run<string>(() => Gadgets.Digest(loco.Loco));
            Report.Note("the server's loco:\n" + host);

            await pair.Client.Quit();
            await Task.Delay(4000);   // the host notices the departure
            await pair.Rejoin();

            string client = await pair.Client.Run<string>(s => Gadgets.DigestWhenEqual(s, loco.Loco, host, 20f));
            if (client != host)
                Report.Note("the client's loco:\n" + client);
            Expect.Equal(host, client, "after joining: the client's gadgets on the loco, against the server's");
            Expect.Equal(built.Installed.Length, await pair.Client.Run<int>(() => Gadgets.CountInstalled(built.Installed)),
                "after joining: the gadgets the client has as installed (item inactive, projection on a car)");
        }

        public sealed class Built
        {
            public ushort[] Installed;
            public string[] PersistentIds;
            public string[] Notes;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: the gadgets on <paramref name="loco"/>, as vanilla's tools leave them (see the
            /// scenario). The parts it can't build (no such gadget in this game) are left out with a note.
            /// </summary>
            [Step]
            public static IEnumerator Build(Step<Built> step, string loco)
            {
                var client = Gadgets.Client();
                TrainCar car = CarSpawner.Instance.AllCars.First(c => c != null && c.ID == loco);
                Vector3 beside = client.WorldPosition + Vector3.right * 1.5f + Vector3.up;
                var notes = new List<string>();
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

                GadgetBase simple = null, sw = null, light = null, plate = null, mounted = null, removed = null;
                yield return Install(Gadgets.SimpleSpec(), at(0f, 0f), g => simple = g);

                InventoryItemSpec switchSpec = Spec(g => g is GadgetSwitch && !(g is AlternatingController));
                InventoryItemSpec lightSpec = Spec(g => g is GadgetLight);
                if (switchSpec != null && lightSpec != null)
                {
                    yield return Install(switchSpec, at(0.3f, 0f), g => sw = g);
                    yield return Install(lightSpec, at(0.6f, 0f), g => light = g);
                    if (sw.wiring.TryGetCompatiblePorts(light.wiring, out var a, out var b))
                        GadgetWiringModule.WireLinkPort.Wire(a, b);
                    else
                        notes.Add($"{switchSpec.ItemPrefabName} and {lightSpec.ItemPrefabName} have no compatible ports: not wired");
                    if (sw is TrainCarCustomization.TrainCarCustomizerBase s) s.MakeSoldered();
                    if (light is TrainCarCustomization.TrainCarCustomizerBase l) l.MakeSoldered();
                    ((GadgetSwitch)sw).SetOutputValue(0.5f);
                }
                else
                    notes.Add("no switch or no light in this game: wiring left out");

                InventoryItemSpec plateSpec = Spec(g => g.GetComponent<Mount>() != null && g.GetComponent<Drillable>() is { MountPointCount: >= 2 });
                InventoryItemSpec mountableSpec = Spec(g => g.RequiredMountPoints > 0 && g.GetComponent<Mount>() == null);
                if (plateSpec != null)
                {
                    yield return Install(plateSpec, at(0f, 0.4f), g => plate = g);
                    Drillable drillable = plate.GetComponent<Drillable>();
                    drillable.SetMountPointState(0, MountPoint.States.Mounted);
                    drillable.SetMountPointState(1, MountPoint.States.Mounted);
                    if (mountableSpec != null)
                    {
                        yield return Install(mountableSpec, at(0f, 0.45f), g => mounted = g);
                        plate.GetComponent<Mount>().MountGadget(mounted);
                    }
                    else
                        notes.Add("no gadget that needs a mount in this game: mount left out");

                    // A bolted plate taken off leaves its holes in the car.
                    yield return Install(plateSpec, at(0f, 0.8f), g => removed = g);
                    removed.GetComponent<Drillable>().SetMountPointState(0, MountPoint.States.Mounted);
                    yield return null;
                    removed.Remove(reparentToTrainCar: false);
                }
                else
                    notes.Add("no plate with two mount points in this game: plates and holes left out");
                for (int i = 0; i < 10; i++) yield return null;

                step.Result = new Built
                {
                    Installed = items.Where(i => i.GetComponent<GadgetItem>().Gadget.IsLinked).Select(i => i.NetId).ToArray(),
                    PersistentIds = items.Select(i => i.GetComponent<Multiplayer.Server.Items.PersistentWorldItemIdentity>().Value.ToString("D")).ToArray(),
                    Notes = notes.Prepend($"built on {loco}: {string.Join(", ", new[] { simple, sw, light, plate, mounted }.Where(g => g != null).Select(g => g.name))}; a removed plate left {car.Customization.Holes.Count()} holes").ToArray()
                };
            }
        }
    }
}
