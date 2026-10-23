using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.Customization;
using DV.Customization.Gadgets;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// An installed gadget is an installed gadget on the server, before and after a save (gadgets.md,
    /// PR 1). Before, an installed gadget's inactive item was registered as a world item at its stale
    /// pose: clients near it got a loose copy, Lost and Found could take it, and it was saved twice.
    /// The server installs two gadgets owned by the client, the way vanilla does once its checks pass:
    /// one on a loco, one on the ground (which a save from single player can hold; gadgets go on
    /// vehicles only in multiplayer), and saves in the same frame. Then: the one on the loco is
    /// Installed on that car, owned by the client, and the client has no loose copy of it; after a
    /// restart on the save, it is still installed on the loco, once, owned by the client, not in Lost
    /// and Found, and still not loose on the client; the one on the ground is still installed on the
    /// world, its ledger home `Installed(:global:)`, owned by the client, not in Lost and Found, and
    /// installed on the client too (OI-GAD-10). Finally the loco is deleted: the gadget comes off as a
    /// world item at its pose, still the client's.
    /// </summary>
    [Scenario("gadget-save", "Installed gadgets stay installed on their car or the world across a save and restart, owned and never loose on a client; a deleted car's gadget comes off as its owner's")]
    [Needs(Need.RestartsGames)]
    [Budget(1200)]
    public sealed class GadgetSave : Scenario<GadgetSave.Options>
    {
        public sealed class Options
        {
            /// <summary>The session (or save) to load.</summary>
            public string Session = "DVMP Test Career 2";
            /// <summary>The loco the gadget goes on.</summary>
            public string Loco = "LocoDE2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            HeldLoco loco = await pair.Host.Run<HeldLoco>(s => Cars.SpawnHeldLoco(s, o.Loco));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars)));
            Installed set = await pair.Host.Run<Installed>(s => InGame.InstallAndSave(s, loco.Loco));
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(new[] { set.OnCar, set.OnGround })));
            Report.Note($"{set.Prefab} installed on {loco.Loco} ({set.OnCar}) and on the ground ({set.OnGround}), " +
                        $"owned by the client ({set.ClientGuid}); saved");

            GadgetRecord before = await pair.Host.Run<GadgetRecord>(() => Gadgets.Record(set.OnCar));
            Expect.Equal("Installed", before.Placement, "before the save: the loco's gadget's placement");
            Expect.Equal(set.CarGuid, before.CarGuid, "before the save: the car its record names");
            Expect.Equal(set.ClientGuid, before.OwnerGuid, "before the save: its owner");
            Expect.That(!await pair.Client.Run<bool>(s => Gadgets.ActiveHere(s, before.NetId, 5f)),
                "before the save: the client has no loose copy of the installed gadget");

            await pair.Restart(o.Session);
            await pair.Host.Run(() => InGame.Settle(10f));

            GadgetRecord onCar = await pair.Host.Run<GadgetRecord>(() => Gadgets.Record(set.OnCar));
            Report.Note($"after the restart, the loco's gadget: {onCar.Placement} on {onCar.Anchor}, owner {onCar.OwnerGuid}, " +
                        $"{onCar.Count} of it");
            Expect.Equal(1, onCar.Count, "after the restart: items with the loco's gadget's id");
            Expect.Equal("Installed", onCar.Placement, "after the restart: the loco's gadget's placement");
            Expect.That(onCar.InstalledInVanilla, "after the restart: vanilla holds the loco's gadget as installed");
            Expect.Equal(set.CarGuid, onCar.Anchor, "after the restart: the car its projection is on");
            Expect.Equal(set.CarGuid, onCar.CarGuid, "after the restart: the car its record names");
            Expect.Equal(set.ClientGuid, onCar.OwnerGuid, "after the restart: its owner");
            Expect.That(!onCar.InLostAndFound, "after the restart: the loco's gadget is not in Lost and Found");
            Expect.That(!onCar.Active, "after the restart: the installed gadget's item is inactive on the server");
            Expect.That(!await pair.Client.Run<bool>(s => Gadgets.ActiveHere(s, onCar.NetId, 5f)),
                "after the restart: the client has no loose copy of the installed gadget");

            GadgetRecord onGround = await pair.Host.Run<GadgetRecord>(() => Gadgets.Record(set.OnGround));
            Report.Note($"after the restart, the ground's gadget: {onGround.Placement}, anchor '{onGround.Anchor}', owner {onGround.OwnerGuid}");
            Expect.Equal(1, onGround.Count, "after the restart: items with the ground gadget's id");
            Expect.Equal("Installed", onGround.Placement, "after the restart: the ground gadget's placement");
            Expect.That(onGround.InstalledInVanilla, "after the restart: vanilla holds the ground gadget as installed");
            Expect.Equal(":global:", onGround.Anchor, "after the restart: the ground gadget is on the world");
            Expect.Equal(set.ClientGuid, onGround.OwnerGuid, "after the restart: the ground gadget's owner");
            Expect.That(!onGround.InLostAndFound, "after the restart: the ground gadget is not in Lost and Found");
            LedgerEntry groundEntry = await pair.Host.Run(() => Owned.Entry(set.OnGround));
            Expect.Equal("Installed :global:", groundEntry.Where, "after the restart: the ground gadget's ledger home");
            Expect.Equal(1, await pair.Client.Run<int>(() => Gadgets.CountInstalled(new[] { onGround.NetId })),
                "after the restart: the client has the ground gadget installed");
            // OI-LIST-21: the owner's list names both installs.
            string carRow = await pair.Host.Run(() => LedgerRows.InGame.RowWhere(set.ClientGuid, set.OnCar));
            Expect.That(carRow.StartsWith("installed on "), "the client's row for the loco's gadget", carRow);
            Expect.Equal("installed in the world", await pair.Host.Run(() => LedgerRows.InGame.RowWhere(set.ClientGuid, set.OnGround)),
                "the client's row for the ground gadget");

            string[] left = await pair.Host.Run<string[]>(s => Cars.Delete(s, loco.Cars));
            Expect.Zero(left.Length, "cars left after deleting the loco");
            GadgetRecord deleted = await pair.Host.Run<GadgetRecord>(() => Gadgets.Record(set.OnCar));
            Report.Note($"after deleting the loco, its gadget: {deleted.Placement}, owner {deleted.OwnerGuid}");
            Expect.That(!deleted.InstalledInVanilla, "after deleting the loco: its gadget came off");
            Expect.That(deleted.Placement != "Installed", "after deleting the loco: its gadget's record is no longer Installed", deleted.Placement);
            Expect.Equal(set.ClientGuid, deleted.OwnerGuid, "after deleting the loco: its gadget's owner");
        }

        public sealed class Installed
        {
            public string Prefab, OnCar, OnGround, CarGuid, ClientGuid;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: two of the simple gadget (Gadgets.SimpleSpec), owned by the client, installed as
            /// vanilla installs: one in <paramref name="loco"/>'s interior, one on the ground beside the
            /// client. Saves in the same frame, before the server takes the ground one off.
            /// </summary>
            [Step]
            public static IEnumerator InstallAndSave(Step<Installed> step, string loco)
            {
                var client = Gadgets.Client();
                InventoryItemSpec spec = Gadgets.SimpleSpec();
                TrainCar car = CarSpawner.Instance.AllCars.First(c => c != null && c.ID == loco);
                Vector3 beside = client.WorldPosition + Vector3.right * 1.5f;
                NetworkedItem onCar = null, onGround = null;
                yield return Gadgets.SpawnOwned(spec, beside + Vector3.up, client, item => onCar = item);
                yield return Gadgets.SpawnOwned(spec, beside + Vector3.up + Vector3.forward, client, item => onGround = item);

                Gadgets.Install(onCar, car.Customization, car.interior.TransformPoint(Vector3.up * 1.5f));
                if (!Physics.Raycast(beside + Vector3.up * 5f, Vector3.down, out RaycastHit ground, 20f))
                    throw new System.Exception("no ground under the client to install on");
                Gadgets.Install(onGround, WorldCustomization.I, ground.point);
                SaveGameManager.Instance.Save(DV.Common.SaveType.Manual);

                step.Result = new Installed
                {
                    Prefab = spec.ItemPrefabName,
                    OnCar = Id(onCar),
                    OnGround = Id(onGround),
                    CarGuid = car.CarGUID,
                    ClientGuid = client.Guid.ToString("D")
                };
                yield return new WaitForSeconds(3);
            }

            /// <summary>Host: lets a freshly loaded world settle.</summary>
            [Step]
            public static IEnumerator Settle(float seconds)
            {
                yield return new WaitForSeconds(seconds);
            }

            private static string Id(NetworkedItem item) =>
                item.GetComponent<Multiplayer.Server.Items.SaveLoad.ItemIdentity>().Value.ToString("D");
        }
    }
}
