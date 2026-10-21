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
    /// Mount points and holes go through the server (gadgets.md, PR 4). The server puts a mount plate
    /// flat on a DE2's cab floor by the client, and a drill, a duct tape and a hole filler by the client;
    /// the client rides the DE2.
    /// - With the drill the client bolts two of the plate's points: bolted on the server and in its game.
    /// - With the tape it tapes a third; with an empty hand it untapes it.
    /// - The server takes the bolted plate off: two holes in both games.
    /// - With the hole filler the client fills one: one hole in both games.
    /// </summary>
    [Scenario("gadget-mounting", "Bolting, taping, untaping and filling holes go through the server; a bolted plate taken off leaves its holes in every game")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class GadgetMounting : Scenario<GadgetMounting.Options>
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
            Require.That(kit.Plate != 0 && kit.Points >= 3, "the server put a plate with three mount points or more on the cab floor", kit.Note);

            await Digests(pair, o, loco.Id, "the plate reached the client");
            using (await pair.Client.Lag(o.Lag))
            {
                if (kit.Drill != 0)
                {
                    await Hold(pair, kit.Drill, loco.Id);
                    await pair.Client.Run(() => InGame.SetPoint(kit.Plate, 0, "Mounted"));
                    await pair.Client.Run(() => InGame.SetPoint(kit.Plate, 1, "Mounted"));
                    if (o.Lag > 0)
                        Expect.Equal("Mounted Mounted None", await pair.Client.Run<string>(s => InGame.PointsWhen(s, kit.Plate, "Mounted Mounted None", 0.1f)),
                            "the client bolted points 0 and 1: its own plate at once, before the server answers (3b)");
                    Expect.Equal("Mounted Mounted None", await pair.Host.Run<string>(s => InGame.PointsWhen(s, kit.Plate, "Mounted Mounted None", o.Seconds)),
                        "the client bolted points 0 and 1: the server's plate");
                    await Digests(pair, o, loco.Id, "the client bolted two points");
                    await pair.Client.Run(() => Items.Drop());
                }
                if (kit.Tape != 0)
                {
                    await Hold(pair, kit.Tape, loco.Id);
                    await pair.Client.Run(() => InGame.SetPoint(kit.Plate, 2, "Taped"));
                    Expect.That((await pair.Host.Run<string>(s => InGame.PointsWhen(s, kit.Plate, "Mounted Mounted Taped", o.Seconds))).EndsWith("Taped"),
                        "the client taped point 2: the server's plate");
                    await pair.Client.Run(() => Items.Drop());
                    await pair.Client.Run(() => InGame.SetPoint(kit.Plate, 2, "None"));
                    Expect.That((await pair.Host.Run<string>(s => InGame.PointsWhen(s, kit.Plate, "Mounted Mounted None", o.Seconds))).EndsWith("None"),
                        "the client untaped point 2: the server's plate");
                    await Digests(pair, o, loco.Id, "the client taped and untaped a point");
                }

                // Off with its bolts: their holes stay, in both games.
                await pair.Host.Run(() => InGame.TakeOff(kit.Plate));
                await Digests(pair, o, loco.Id, "the server took the bolted plate off");
                int holes = await pair.Host.Run<int>(() => InGame.Holes(loco.Id));
                Report.Note($"holes after the plate came off: {holes}");

                if (kit.Filler != 0 && holes > 0)
                {
                    await Hold(pair, kit.Filler, loco.Id);
                    await pair.Client.Run(() => InGame.FillFirstHole(loco.Id));
                    Expect.Equal(holes - 1, await pair.Host.Run<int>(s => InGame.HolesWhen(s, loco.Id, holes - 1, o.Seconds)),
                        "the client filled a hole: the server's holes");
                    await Digests(pair, o, loco.Id, "the client filled a hole");
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
                $"{what}: the client's gadgets and holes on the loco, against the server's");
        }

        public sealed class Kit
        {
            public ushort Plate, Drill, Tape, Filler;
            public int Points;
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

            /// <summary>Host: a mount plate flat on the cab floor (its surfaces switched on to find it), and
            /// a drill, a duct tape and a hole filler by the client, all owned by it.</summary>
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

                NetworkedItem plateItem = null, drill = null, tape = null, filler = null;
                InventoryItemSpec plateSpec = Spec(s => s.GetComponent<GadgetItem>()?.gadgetPrefab is { } g && g.GetComponent<Mount>() != null &&
                                                        g.GetComponent<Drillable>() is { } d && d.MountPointCount >= 3 && !(g is GadgetWithSnapPoint));
                yield return Spawn(plateSpec, beside, i => plateItem = i);
                yield return Spawn(Spec(s => s.GetComponentInChildren<DrillTool>(true) != null), beside + Vector3.forward * 0.5f, i => drill = i);
                yield return Spawn(Spec(s => s.GetComponentInChildren<DuctTape>(true) != null), beside + Vector3.forward * 1f, i => tape = i);
                yield return Spawn(Spec(s => s.GetComponentInChildren<HoleFiller>(true) != null), beside + Vector3.forward * 1.5f, i => filler = i);

                string note = "no plate with three mount points";
                GadgetBase plate = null;
                if (plateItem != null)
                {
                    GadgetColliderHolder[] holders = car.GetComponentsInChildren<GadgetColliderHolder>(true);
                    foreach (var h in holders) h.holder?.SetActive(true);
                    Physics.SyncTransforms();
                    Vector3 from = car.interior.TransformPoint(new Vector3(0f, 2f, 0.5f));
                    if (Physics.Raycast(from, -car.interior.up, out RaycastHit hit, 4f, LayerMask.GetMask("Gadget_Mesh_Placing")))
                    {
                        Quaternion rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(car.interior.forward, hit.normal), hit.normal);
                        Transform frame = car.Customization.GetParentingTransform();
                        plate = GadgetItem.Place(car.Customization, frame.InverseTransformPoint(hit.point),
                            Quaternion.Inverse(frame.rotation) * rotation, plateItem.GetComponent<GadgetItem>(), hit.collider);
                        note = $"{plateItem.name} on {hit.collider.name} at {hit.point - car.interior.position}";
                    }
                    else
                    {
                        note = "no cab floor under the raycast";
                    }
                }
                for (int i = 0; i < 10; i++) yield return null;
                ushort Id(NetworkedItem i) => i != null ? i.NetId : (ushort)0;
                step.Result = new Kit
                {
                    Plate = plate != null && plate.IsLinked ? plateItem.NetId : (ushort)0,
                    Points = plate != null ? plate.GetComponent<Drillable>().MountPointCount : 0,
                    Drill = Id(drill),
                    Tape = Id(tape),
                    Filler = Id(filler),
                    PersistentIds = items.Where(i => i != null)
                        .Select(i => i.GetComponent<Multiplayer.Server.Items.PersistentWorldItemIdentity>().Value.ToString("D")).ToArray(),
                    Note = $"{note}; drill {drill?.name}, tape {tape?.name}, filler {filler?.name}"
                };
            }

            /// <summary>Sets one of the plate's mount points as the player's tool would.</summary>
            [Step]
            public static void SetPoint(ushort plate, int index, string state) =>
                Gadget(plate).GetComponent<Drillable>().SetMountPointState(index, (MountPoint.States)Enum.Parse(typeof(MountPoint.States), state));

            /// <summary>The plate's first three mount point states once they read <paramref name="expected"/>, or after <paramref name="seconds"/>.</summary>
            [Step]
            public static IEnumerator PointsWhen(Step<string> step, ushort plate, string expected, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    Drillable drillable = Gadget(plate)?.GetComponent<Drillable>();
                    step.Result = drillable == null ? "(no plate)" : string.Join(" ", Enumerable.Range(0, 3).Select(i => drillable.GetMountPointState(i).ToString()));
                    if (step.Result == expected)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }

            /// <summary>Host: takes the plate off as the hammer would.</summary>
            [Step]
            public static void TakeOff(ushort plate) => Gadget(plate)?.Remove();

            [Step]
            public static int Holes(string loco) => Dvmp.Lib.Trains.Find(loco).Customization.Holes.Count();

            [Step]
            public static IEnumerator HolesWhen(Step<int> step, string loco, int expected, float seconds)
            {
                float end = Time.realtimeSinceStartup + seconds;
                do
                {
                    step.Result = Holes(loco);
                    if (step.Result == expected)
                        yield break;
                    yield return new WaitForSecondsRealtime(0.25f);
                } while (Time.realtimeSinceStartup < end);
            }

            /// <summary>Fills the car's first hole as the hole filler would.</summary>
            [Step]
            public static void FillFirstHole(string loco)
            {
                Customization custom = Dvmp.Lib.Trains.Find(loco).Customization;
                custom.RemoveHole(custom.Holes.First());
            }
        }
    }
}
