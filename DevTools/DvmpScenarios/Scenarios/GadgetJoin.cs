using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A player joining sees the gadgets installed on the trains as the server has them (gadgets.md,
    /// PR 2). The server fills a car with gadgets the way vanilla's tools leave them: a simple
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
            /// <summary>The car the gadgets go on (default: the uncoupled car nearest the client). A car
            /// spawned for the run isn't used: spawned beside a parked car it couples, and a rejoin onto a
            /// coupling made that way fails on the train baseline (chain state), which isn't this test.</summary>
            public string Car = "";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string car = string.IsNullOrEmpty(o.Car) ? await pair.Host.Run<string>(() => InGame.NearestUncoupledCar()) : o.Car;
            pair.Cleanup(() => pair.Host.Run(() => InGame.ClearHoles(car)));
            Built built = await pair.Host.Run<Built>(s => InGame.Build(s, car));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(built.PersistentIds)));
            foreach (string note in built.Notes)
                Report.Note(note);

            string host = await pair.Host.Run<string>(() => Gadgets.Digest(car));
            Report.Note("the server's car:\n" + host);

            await pair.Client.Quit();
            await Task.Delay(4000);   // the host notices the departure
            await pair.Rejoin();

            string client = await pair.Client.Run<string>(s => Gadgets.DigestWhenEqual(s, car, host, 20f));
            if (client != host)
                Report.Note("the client's car:\n" + client);
            Expect.Equal(host, client, "after joining: the client's gadgets on the car, against the server's");
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
            /// <summary>Host: the ID of the car nearest the client that is coupled to nothing and on the rails.</summary>
            [Step]
            public static string NearestUncoupledCar()
            {
                Vector3 client = Gadgets.Client().WorldPosition;
                return CarSpawner.Instance.AllCars
                    .Where(c => c != null && !c.derailed && c.Customization != null &&
                                c.frontCoupler.coupledTo == null && c.rearCoupler.coupledTo == null)
                    .OrderBy(c => (c.transform.position - client).sqrMagnitude)
                    .Select(c => c.ID).FirstOrDefault() ?? throw new Exception("no uncoupled car on the rails");
            }

            /// <summary>Host: takes the holes the run made out of the car again (the session's save keeps the car).</summary>
            [Step]
            public static void ClearHoles(string carId) =>
                CarSpawner.Instance.AllCars.FirstOrDefault(c => c != null && c.ID == carId)?.Customization.ClearHoles();

            /// <summary>
            /// Host: the gadgets on <paramref name="carId"/>, as vanilla's tools leave them (see the
            /// scenario). The parts it can't build (no such gadget in this game) are left out with a note.
            /// </summary>
            [Step]
            public static IEnumerator Build(Step<Built> step, string carId)
            {
                var client = Gadgets.Client();
                TrainCar car = CarSpawner.Instance.AllCars.First(c => c != null && c.ID == carId);
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

                // A prefab's mount points are set up only once it's live: count them after installing.
                InventoryItemSpec plateSpec = Spec(g => g.GetComponent<Mount>() != null && !(g is GadgetWithSnapPoint));
                InventoryItemSpec mountableSpec = Spec(g => g.RequiredMountPoints > 0 && g.GetComponent<Mount>() == null);
                if (plateSpec != null)
                {
                    yield return Install(plateSpec, at(0f, 0.4f), g => plate = g);
                    Drillable drillable = plate.GetComponent<Drillable>();
                    for (int i = 0; i < Math.Min(2, drillable?.MountPointCount ?? 0); i++)
                        drillable.SetMountPointState(i, MountPoint.States.Mounted);
                    if ((drillable?.MountPointCount ?? 0) < 2)
                        notes.Add($"{plateSpec.ItemPrefabName} has {drillable?.MountPointCount ?? 0} mount points: bolted what there is");
                    if (mountableSpec != null)
                    {
                        yield return Install(mountableSpec, at(0f, 0.45f), g => mounted = g);
                        plate.GetComponent<Mount>().MountGadget(mounted);
                    }
                    else
                        notes.Add("no gadget that needs a mount in this game: mount left out");

                    // A bolted plate taken off leaves its holes in the car.
                    yield return Install(plateSpec, at(0f, 0.8f), g => removed = g);
                    if (removed.GetComponent<Drillable>() is { MountPointCount: > 0 } bolt)
                        bolt.SetMountPointState(0, MountPoint.States.Mounted);
                    yield return null;
                    removed.Remove(reparentToTrainCar: false);
                }
                else
                    notes.Add("no mount plate in this game: plates and holes left out");
                for (int i = 0; i < 10; i++) yield return null;

                step.Result = new Built
                {
                    Installed = items.Where(i => i.GetComponent<GadgetItem>().Gadget.IsLinked).Select(i => i.NetId).ToArray(),
                    PersistentIds = items.Select(i => i.GetComponent<Multiplayer.Server.Items.SaveLoad.ItemIdentity>().Value.ToString("D")).ToArray(),
                    Notes = notes.Prepend($"built on {carId}: {string.Join(", ", new[] { simple, sw, light, plate, mounted }.Where(g => g != null).Select(g => g.name))}; a removed plate left {car.Customization.Holes.Count()} holes").ToArray()
                };
            }
        }
    }
}
