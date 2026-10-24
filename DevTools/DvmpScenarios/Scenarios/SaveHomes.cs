using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DV.Customization;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Chat;
using Multiplayer.Server.Items.Access;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// What a save keeps of the homes the other scenarios don't save (docs/design/items/save-load.md,
    /// "Loading"). Before the save, the client has:
    /// - a mug of an absent player's in a slot (borrowed when the server saves: BR SL1, OI-JOIN-08,
    ///   OI-PER-07): at the load it goes to its owner's Lost and Found (SaveRecovery), still theirs;
    /// - an unowned mug in a slot (OI-PER-19): it comes back in that slot, still nobody's;
    /// - a scene-authored item in a slot (OI-PER-16; not office stock, which the scene stocks again once
    ///   taken: the nearest is a fire extinguisher): it comes back in that slot, once, and the scene
    ///   doesn't make it again, through two restarts;
    /// - an EOT lantern given to it ("theirs to keep", as the admin grant gives it: shops are paused)
    ///   on a flatbed's coupler (Attached, OI-PER-13): it comes back attached to that car, the client's;
    /// - a mug of its own in a loco's cab (OI-PER-12) and a gadget of its own installed on the loco
    ///   (OI-LF-22), and the save is written without the loco in vanilla's cars, as a car that didn't
    ///   load leaves it: the mug goes to its Lost and Found (SaveRecovery), the gadget too
    ///   (GadgetDestinationMissing).
    /// A second save and restart must keep the slots and the lantern as they came back.
    /// </summary>
    [Scenario("save-homes", "A save keeps carried unowned and scene items, an attached EOT lantern; a borrowed item and a gone car's cab item and gadget go to Lost and Found")]
    [Needs(Need.RestartsGames)]
    [Budget(1800)]
    public sealed class SaveHomes : Scenario<SaveHomes.Options>
    {
        public sealed class Options
        {
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
        }

        // Where every run starts (ScenarioRun.ResetAsync).
        private const float StartX = 7190f, StartZ = 6880f;

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            List<string> ids = new();
            List<string> gadgets = new();
            List<string> cars = new();
            // By id at the end: each restart gives the items new net IDs.
            pair.Cleanup(async () =>
            {
                var now = new List<ushort>();
                foreach (string id in ids)
                {
                    ushort netId = await pair.Host.Run(() => Owned.NetIdOf(id));
                    if (netId != 0) now.Add(netId);
                }
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, now.ToArray()), 60);
                if (gadgets.Count > 0) await pair.Host.Run(() => Gadgets.DestroyByPersistentId(gadgets.ToArray()));
                if (cars.Count > 0) await pair.Host.Run<string[]>(s => Cars.Delete(s, cars.ToArray()));
            });

            // The scene item first, from where the scene has it: the rest are made by the start.
            SceneItem scene = await pair.Host.Run(() => InGame.SceneItemBy(StartX, StartZ));
            Report.Note($"scene item: {scene}");
            await pair.Client.Run(() => Players.TeleportToGround(scene.Pos.X + 1.2f, scene.Pos.Z), 60);
            int sceneSlot = await pair.Client.Run<int>(s => Items.Stash(s, scene.NetId, false), 60);
            string sceneId = await pair.Host.Run(() => Owned.IdOf(scene.NetId));
            Require.That(sceneId.Length > 0, $"the {scene.Prefab} has an id", scene.NetId.ToString());
            ids.Add(sceneId);
            await pair.Client.Run(() => Players.TeleportToGround(StartX, StartZ), 60);

            ushort borrowed = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            AbsentOwner.Absent absent = await pair.Host.Run(() => AbsentOwner.InGame.GiveToAbsent(borrowed));
            ids.Add(absent.Id);
            int borrowedSlot = await pair.Client.Run<int>(s => Items.Stash(s, borrowed, false), 60);

            ushort found = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "client"));
            int foundSlot = await pair.Client.Run<int>(s => Items.Stash(s, found, false), 60);
            string foundId = await pair.Host.Run(() => Owned.IdOf(found));
            ids.Add(foundId);

            string flatbed = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "FlatbedEmpty", "client"));
            cars.Add(flatbed);
            string flatbedGuid = await pair.Host.Run(() => InGame.CarGuid(flatbed));
            ushort lantern = await pair.Host.Run<ushort>(s => InGame.Grant(s, "EOTLantern"));
            string lanternId = await pair.Host.Run(() => Owned.IdOf(lantern));
            ids.Add(lanternId);
            string attached = await pair.Client.Run<string>(s => InGame.AttachToCoupler(s, lantern, flatbed), 90);
            Report.Note("the client attached the lantern: " + attached);
            await Until.Value(() => pair.Host.Run(() => Items.Record(lantern)), r => r.Placement == "Attached", 20,
                "the host has the lantern attached");

            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "client"));
            cars.Add(loco);
            string locoGuid = await pair.Host.Run(() => InGame.CarGuid(loco));
            ushort cab = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(cab), 60);   // the client's before it boards with it
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, cab, "drop", 90), 90);
            string cabId = await pair.Host.Run(() => Owned.IdOf(cab));
            ids.Add(cabId);
            string gadget = await pair.Host.Run<string>(s => InGame.InstallFor(s, loco));
            gadgets.Add(gadget);
            await pair.Client.Run(() => Players.TeleportToGround(StartX, StartZ), 60);
            await Task.Delay(2000);   // the last moves reach the ledger

            Report.Note("before the save: " + string.Join("; ", await Entries(pair, sceneId, absent.Id, foundId, lanternId, cabId, gadget)));
            Require.That((await pair.Host.Run(() => Owned.Entry(cabId))).Where.StartsWith("TrainInterior"), "the cab mug is in the loco");
            Require.That((await pair.Host.Run(() => Owned.Entry(gadget))).Where.StartsWith("Installed " + locoGuid), "the gadget is installed on the loco");
            Require.Equal("Attached", (await pair.Host.Run(() => Owned.Entry(lanternId))).Place, "the lantern's ledger home");
            Require.That(!(await pair.Host.Run(() => Owned.Entry(foundId))).Known, "the found mug is nobody's (no ledger entry)");

            Expect.Equal(1, await pair.Host.Run<int>(s => SaveEdits.SaveWithoutCars(s, new[] { locoGuid })), "cars taken out of the save");
            await pair.Restart(o.Session);
            await Task.Delay(15000);   // the client receives its restored items
            Expect.That(await pair.Host.Run(() => Owned.Loaded()), "the ledger was loaded after the restart");
            Expect.Zero((await pair.Host.Run(() => Cars.Existing(new[] { loco }))).Length, "the loco is gone after the restart");

            // Borrowed when the server saved: its owner's Lost and Found.
            OwnerInfo b = await pair.Host.Run(() => Ownership.ById(absent.Id));
            Report.Note("the borrowed mug: " + b);
            Expect.Equal("LostAndFound", b.Placement, "the borrowed mug, saved in the client's slot");
            Expect.Equal(absent.Owner, b.Owner, "the borrowed mug's owner");
            Expect.Equal("SaveRecovery", await pair.Host.Run(() => InGame.LostReason(absent.Id)), "why the borrowed mug is in Lost and Found");
            Expect.Equal(-1, (await pair.Host.Run(() => Owned.Entry(absent.Id))).Slot, "the borrowed mug has no slot of the client's");

            // The loco's mug and gadget: its car is gone.
            OwnerInfo c = await pair.Host.Run(() => Ownership.ById(cabId));
            Report.Note("the cab mug: " + c);
            Expect.Equal("LostAndFound", c.Placement, "the cab mug, its car gone");
            Expect.Equal(client, c.Owner, "the cab mug's owner");
            Expect.Equal("SaveRecovery", await pair.Host.Run(() => InGame.LostReason(cabId)), "why the cab mug is in Lost and Found");
            GadgetRecord g = await pair.Host.Run<GadgetRecord>(() => Gadgets.Record(gadget));
            Report.Note($"the gadget: {g.Placement}, owner {g.OwnerGuid}, {g.Count} of it, in Lost and Found {g.InLostAndFound}, installed in vanilla {g.InstalledInVanilla}");
            Expect.Equal(1, g.Count, "items with the gadget's id");
            Expect.That(g.InLostAndFound, "the gadget is in Lost and Found, its car gone");
            Expect.Equal(client, g.OwnerGuid, "the gadget's owner");
            Expect.Equal("GadgetDestinationMissing", await pair.Host.Run(() => InGame.LostReason(gadget)), "why the gadget is in Lost and Found");

            await Carried(pair, client, foundId, foundSlot, null, "the found mug", "after the restart");
            await Carried(pair, client, sceneId, sceneSlot, scene.Key, $"the {scene.Prefab}", "after the restart");
            await Lantern(pair, client, lanternId, flatbedGuid, "after the restart");

            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart(o.Session);
            await Task.Delay(15000);
            await Carried(pair, client, foundId, foundSlot, null, "the found mug", "after a second restart");
            await Carried(pair, client, sceneId, sceneSlot, scene.Key, $"the {scene.Prefab}", "after a second restart");
            await Lantern(pair, client, lanternId, flatbedGuid, "after a second restart");
            Expect.Equal("LostAndFound", (await pair.Host.Run(() => Ownership.ById(absent.Id))).Placement, "the borrowed mug, after a second restart");
            Expect.Empty(await LedgerSave.SettledAudit(pair, ids.Concat(gadgets).ToArray()), "what the ledger disagrees with");
        }

        private static async Task<string[]> Entries(Pair pair, params string[] ids) =>
            (await pair.Host.Run(() => Owned.Entries(ids))).Select(e => e.ToString()).ToArray();

        // An item the client carried: back in the same slot, in both games, once, and its owner kept
        // (none for an unowned one, which has no ledger entry). A scene item's scene copy isn't made again.
        private static async Task Carried(Pair pair, string client, string id, int slot, string sceneKey, string what, string when)
        {
            OwnerInfo now = await pair.Host.Run(() => Ownership.ById(id));
            Report.Note($"{what} {when}: {now}");
            if (!Expect.That(now.Exists, $"{what} {when}: there", id))
                return;
            await pair.Host.Run(() => Ledger.Track(now.NetId));
            Expect.Equal("PlayerInventory", now.Placement, $"{what} {when}: where it is");
            Expect.Equal(client, now.Holder, $"{what} {when}: whose inventory");
            Expect.Equal(slot, (await pair.Host.Run(() => Ledger.Where(now.NetId))).Slot, $"{what} {when}: the server's slot");
            int local = -1;
            for (int i = 0; i < 20 && local != slot; i++)
            {
                local = await pair.Client.Run(() => Ledger.LocalSlot(now.NetId));
                if (local != slot) await Task.Delay(1000);
            }
            Expect.Equal(slot, local, $"{what} {when}: the client's slot");
            Expect.Equal(1, await pair.Host.Run(() => InGame.CountWithId(id)), $"{what} {when}: records with its id");
            if (sceneKey != null)
            {
                Expect.Empty(await pair.Host.Run(() => InGame.AuthoredCopies(sceneKey, now.NetId)), $"{what} {when}: copies the scene made again (host)");
                Expect.Empty(await pair.Client.Run(() => InGame.AuthoredCopies(sceneKey, now.NetId)), $"{what} {when}: copies the scene made again (client)");
            }
        }

        private static async Task Lantern(Pair pair, string client, string id, string carGuid, string when)
        {
            LedgerEntry entry = await pair.Host.Run(() => Owned.Entry(id));
            Report.Note($"the lantern {when}: {entry}");
            Expect.That(entry.Live, $"the lantern {when}: live in the ledger");
            Expect.Equal(client, entry.Owner, $"the lantern {when}: its owner");
            Expect.Equal("Attached", entry.Place, $"the lantern {when}: its ledger home");
            Expect.Equal(carGuid, entry.Anchor, $"the lantern {when}: the car it's attached to");
            OwnerInfo now = await pair.Host.Run(() => Ownership.ById(id));
            Expect.Equal("Attached", now.Placement, $"the lantern {when}: the server's record");
            if (now.Exists) await pair.Host.Run(() => Ledger.Track(now.NetId));
        }

        public sealed class SceneItem
        {
            public ushort NetId;
            public string Prefab, Key;
            public Abs Pos;
            public override string ToString() => $"{Prefab} {NetId} at {Pos?.X:0.#}, {Pos?.Z:0.#} (scene key {Key})";
        }

        // ---- runs inside the games
        public static class InGame
        {
            // Small loose things a player would pick up; not mugs (every run's reset takes mugs),
            // papers (job paperwork looks alike) or a container.
            private static readonly string[] Pickable = { "FireExtinguisher", "Calculator", "Ashtray", "Pen", "Lamp", "Clipboard", "Cup2", "Lighter" };

            /// <summary>
            /// Host: the scene-authored item nearest the point, of a kind a player pockets, that isn't a
            /// fixture or office or shop stock (a taken one of those is an ordinary item, and the scene
            /// stocks its slot again: OfficeStockReplenishment) or a mug (the run's reset destroys mugs).
            /// </summary>
            [Step]
            public static SceneItem SceneItemBy(float x, float z)
            {
                Vector3 at = new(x, 0f, z);
                NetworkedItem item = UnityEngine.Resources.FindObjectsOfTypeAll<NetworkedItem>()
                    .Where(n => n != null && n.gameObject.scene.IsValid() && n.IsSceneAuthored && n.NetId != 0 &&
                                Pickable.Contains(n.name.Replace("(Clone)", "")) &&
                                !NetworkedItemManager.IsEligibleAuthoredOfficeProp(n) && !n.IsAuthoredShopSlot &&
                                LiveItems.TryGet(n.NetId, out var r) && r.Placement == ItemPlacementKind.World &&
                                !ItemAccessServer.Fixtures.Contains(r.PrefabName, n.AuthoredItemKey))
                    .OrderBy(n => Vector3.Distance(Flat(Dev.Abs(n.transform.position)), at))
                    .FirstOrDefault() ?? throw new Exception("no scene-authored item to pick up");
                return new SceneItem { NetId = item.NetId, Prefab = item.name, Key = item.AuthoredItemKey, Pos = Positions.Of(item.transform.position) };
            }

            private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

            /// <summary>
            /// The live items carrying the scene key other than <paramref name="carried"/>: copies the scene
            /// made again. The scene's own object, kept inactive as a tombstone (unregistered, or its record
            /// Destroyed), is not one.
            /// </summary>
            [Step]
            public static string[] AuthoredCopies(string key, ushort carried) =>
                UnityEngine.Resources.FindObjectsOfTypeAll<NetworkedItem>()
                    .Where(n => n != null && n.gameObject.scene.IsValid() && n.AuthoredItemKey == key && n.NetId != carried &&
                                (n.gameObject.activeInHierarchy || n.NetId != 0) &&
                                !(LiveItems.TryGet(n.NetId, out var r) && r.Placement == ItemPlacementKind.Destroyed))
                    .Select(n => $"{n.name} {n.NetId} at {Dev.Abs(n.transform.position)}")
                    .ToArray();

            /// <summary>Host: records with this id.</summary>
            [Step]
            public static int CountWithId(string id) => LiveItems.GetAll().Count(r => r.PersistentItemId == Guid.Parse(id));

            [Step]
            public static string CarGuid(string car) => Trains.Find(car).CarGUID;

            /// <summary>Host: the reason the item's Lost and Found entry gives, or "".</summary>
            [Step]
            public static string LostReason(string id) =>
                LostAndFoundServer.TryGetEntry(Guid.Parse(id), out var entry) ? entry.Reason.ToString() : "";

            /// <summary>Host: gives the client an item of <paramref name="prefab"/> to keep, as the admin
            /// grant does (AdminItemGrants.Spawn: owned by them, into their inventory). Result: its net ID.</summary>
            [Step]
            public static IEnumerator Grant(Step<ushort> step, string prefab)
            {
                var player = Gadgets.Client();
                InventoryItemSpec spec = AdminItemGrants.Find(prefab, out _) ?? throw new Exception("no item " + prefab);
                HashSet<ushort> before = new(LiveItems.GetAll().Select(r => r.NetId));
                yield return AdminItemGrants.Spawn(player, new[] { spec });
                LiveItems.LiveItem made = LiveItems.GetAll().FirstOrDefault(r => !before.Contains(r.NetId) && r.PrefabName == prefab && r.Owner == player.Guid)
                    ?? throw new Exception($"the {prefab} given to the client has no record");
                Items.Track(made.NetId);
                step.Result = made.NetId;
            }

            /// <summary>
            /// Client: takes the item out of its inventory into its hand and snaps it onto the car's front
            /// coupler, as a player hanging an EOT lantern does. Result: its state then.
            /// </summary>
            [Step]
            public static IEnumerator AttachToCoupler(Step<string> step, ushort netId, string car)
            {
                yield return Items.Arrive(netId);
                NetworkedItem.TryGet(netId, out NetworkedItem item);
                TrainCar c = Trains.Find(car);
                Vector3 coupler = Dev.Abs(c.frontCoupler.transform.position);
                yield return LocalPlayer.TeleportToGround(coupler.x + 1.5f, coupler.z);
                yield return Wait.Until(() => c.physicsLod != null && c.physicsLod.GetCouplerSnapPoints().Any(), 20f, "the car's coupler snap points");
                if (DV.InventorySystem.Inventory.Instance.IndexOf(item.gameObject) >= 0 &&
                    DV.InventorySystem.Inventory.Instance.DropItemFromHandsOrInventory(item.gameObject) == null)
                    throw new Exception("couldn't take the lantern out of the inventory");
                for (int i = 0; i < 30; i++) yield return null;
                // From the hand: the server takes an attach only from a player holding the item.
                yield return Hands.Pickup(netId);
                for (int i = 0; i < 30; i++) yield return null;
                var point = c.physicsLod.GetCouplerSnapPoints().OrderByDescending(p => p.IsFront).First();
                item.Item.ItemRigidbody.isKinematic = false;
                if (!point.SnapItem(item.Item, false)) throw new Exception("the coupler refused the lantern");
                for (int i = 0; i < 120; i++) yield return null;
                step.Result = item.GetItemState().ToString();
            }

            /// <summary>Host: the simple gadget (Gadgets.SimpleSpec), the client's, installed in the
            /// loco's interior as vanilla installs. Result: its persistent id.</summary>
            [Step]
            public static IEnumerator InstallFor(Step<string> step, string loco)
            {
                var client = Gadgets.Client();
                TrainCar car = Trains.Find(loco);
                NetworkedItem gadget = null;
                yield return Gadgets.SpawnOwned(Gadgets.SimpleSpec(), client.WorldPosition + Vector3.right * 1.5f + Vector3.up, client, item => gadget = item);
                Gadgets.Install(gadget, car.Customization, car.interior.TransformPoint(Vector3.up * 1.5f));
                for (int i = 0; i < 60; i++) yield return null;
                step.Result = gadget.GetComponent<ItemIdentity>().Value.ToString("D");
            }
        }
    }
}
