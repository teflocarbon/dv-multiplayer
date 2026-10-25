using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using DV.Items;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Attached items and Lost and Found (R6 C3; OI-LF-04, OI-LIST-14, OI-LIST-06). Two end-of-train
    /// lanterns hang on two flatbeds' couplers: one of a player who isn't here, one the client's own.
    /// The client's list says its lantern is "on the end of car" its flatbed. With the client nearby
    /// nothing is collected. Once everyone walks away, the absent player's lantern is collected into
    /// their Lost and Found, off its coupler; the online client's own stays on its coupler. Back
    /// beside it, the client retrieves its own lantern from its list: it comes off the coupler into
    /// its Lost and Found, and the coupler is free in the client's game. A hanger (a gadget with a snap
    /// point, installed in a loco's cab) holds another item of the absent player's: it is collected
    /// too, off the hanger, which is free again.
    /// </summary>
    [Scenario("attached-offline", "An absent owner's lantern on a coupler and item on a hanger are collected off them; the online owner's stays, and its retrieval detaches it (OI-LF-04, OI-LIST-14)")]
    [Budget(600)]
    public sealed class AttachedOffline : Scenario<AttachedOffline.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 50, Nearby = 100, Grace = 1 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string theirs = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "FlatbedEmpty", "client"));
            string mine = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "FlatbedEmpty", "client"));
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { theirs, mine, loco })));
            string client = await pair.Host.Run(() => Owned.ClientIdentity());

            ushort absent = await pair.Host.Run<ushort>(s => SaveHomes.InGame.Grant(s, "EOTLantern"));
            Report.Note("the absent player's lantern: " + await pair.Client.Run<string>(s => SaveHomes.InGame.AttachToCoupler(s, absent, theirs), 90));
            ushort own = await pair.Host.Run<ushort>(s => SaveHomes.InGame.Grant(s, "EOTLantern"));
            string ownId = await pair.Host.Run(() => Owned.IdOf(own));
            Report.Note("the client's lantern: " + await pair.Client.Run<string>(s => SaveHomes.InGame.AttachToCoupler(s, own, mine), 90));
            await Until.Value(() => pair.Host.Run(() => Items.Record(absent)), r => r.Placement == "Attached", 20, "the host has the first lantern attached");
            await Until.Value(() => pair.Host.Run(() => Items.Record(own)), r => r.Placement == "Attached", 20, "the host has the client's lantern attached");
            AbsentOwner.Absent a = await pair.Host.Run(() => AbsentOwner.InGame.GiveToAbsent(absent));
            Require.That(!a.OwnerConnected, "the first lantern's owner is an identity with no session", a.Owner);
            Hung hung = await pair.Host.Run<Hung>(s => InGame.Hang(s, loco), 120);
            Report.Note($"on the hanger {hung.Hanger} in {loco}: a {hung.Prefab} ({hung.Item}), {hung.State}");
            await Until.Value(() => pair.Host.Run(() => Items.Record(hung.Item)), r => r.Placement == "SnappedAttachment", 20, "the host has the item on the hanger");
            AbsentOwner.Absent b = await pair.Host.Run(() => AbsentOwner.InGame.GiveToAbsent(hung.Item));

            OwnedList.Rows rows = await pair.Host.Run(() => OwnedList.InGame.ServerRows(client));
            int at = System.Array.IndexOf(rows.Ids, ownId);
            if (Expect.That(at >= 0, "the client's lantern is in its list", ownId))
                Expect.Equal("on the end of car " + mine, rows.Wheres[at], "the client's lantern's place in its list (OI-LIST-06)");

            await Task.Delay(5000);   // longer than the grace period, with the client nearby
            Expect.That(!(await pair.Host.Run(() => Items.Record(absent))).Lost, "with the client nearby the absent player's lantern isn't collected");
            Expect.That(!(await pair.Host.Run(() => Items.Record(hung.Item))).Lost, "with the client nearby the item on the hanger isn't collected");

            await pair.Client.Run<Abs>(s => Walk.Away(s, 400, 0));
            if (!pair.Dedicated) await pair.Host.Run<Abs>(s => Walk.Away(s, 400, 20));
            await Until.Value(() => pair.Host.Run(() => Items.Record(absent)), r => r.Lost, 30, "the absent player's lantern collected");
            Expect.Equal(a.Id, await pair.Host.Run(() => Owned.LostEntryId(absent)), "the absent player's lantern's entry (its own id)");
            Expect.Equal("LostAndFound", (await pair.Host.Run(() => Owned.Entry(a.Id))).Place, "the absent player's lantern's ledger home");
            await Until.Value(() => pair.Host.Run(() => Items.Record(hung.Item)), r => r.Lost, 30, "the absent player's item on the hanger collected");
            Expect.Equal("LostAndFound", (await pair.Host.Run(() => Owned.Entry(b.Id))).Place, "the item on the hanger's ledger home");
            Expect.That(!await pair.Host.Run(() => InGame.HangerHolds(hung.HangerNetId)), "the hanger is free on the host after the collection");
            ItemRecord ownAway = await pair.Host.Run(() => Items.Record(own));
            Expect.That(!ownAway.Lost && ownAway.Placement == "Attached", "the online client's own lantern stays on its coupler", ownAway.ToString());

            await pair.Client.Run<Abs>(s => Walk.Away(s, -400, 0));
            await Task.Delay(3000);
            Expect.Equal(0, await pair.Client.Run<int>(s => InGame.OnCouplers(s, theirs), 30), "lanterns on the first flatbed's couplers in the client's game (the collected one came off)");
            uint handle = await pair.Host.Run(() => OwnedRetrieve.InGame.HandleOf(client, ownId));
            Require.That(handle != 0, "the client's lantern has a row in its list");
            OwnedRetrieve.Result r = await pair.Client.Run<OwnedRetrieve.Result>(s => OwnedRetrieve.InGame.Retrieve(s, handle), 30);
            Expect.That(r.Accepted, "the client's retrieval of its attached lantern is accepted", r.Reason);
            ItemRecord retrieved = await Until.Value(() => pair.Host.Run(() => Items.Record(own)), v => v.Lost, 20, "the client's lantern in its Lost and Found");
            Report.Note("after the retrieval: " + retrieved);
            Expect.Equal(ownId, await pair.Host.Run(() => Owned.LostEntryId(own)), "the client's lantern's entry (its own id)");
            Expect.Equal(0, await pair.Client.Run<int>(s => InGame.OnCouplers(s, mine), 30), "lanterns on the client's flatbed's couplers in its game after the retrieval");
            int hostCouplers = await pair.Host.Run<int>(s => InGame.OnCouplers(s, mine), 30);
            Report.Note($"lanterns on the client's flatbed's couplers on the host: {hostCouplers} (-1: not loaded there)");
            Expect.That(hostCouplers <= 0, "the host has no lantern left on the client's flatbed's couplers", hostCouplers.ToString());
        }

        public sealed class Hung
        {
            public ushort Item, HangerNetId;
            public string Hanger = "", Prefab = "", State = "";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: a hanger (a gadget with a snap point that installs without a mount), the client's,
            /// installed in the loco's cab as vanilla installs, and an item that hangs on it (the first
            /// prefab whose spec allows the hanger's snap type, leaving out one with a magazine: the
            /// Boombox's cassette is another item) spawned and hung there. A prefab has no
            /// SnappableItem until its ItemBase sets one up from the spec.
            /// </summary>
            [Step]
            public static IEnumerator Hang(Step<Hung> step, string loco)
            {
                var client = Gadgets.Client();
                TrainCar car = Trains.Find(loco);
                InventoryItemSpec hangerSpec = DV.Globals.G.Items.items
                    .Where(spec => spec != null && spec.GetComponent<GadgetItem>()?.gadgetPrefab is { } g &&
                                   g.RequiredMountPoints == 0 && g.GetComponent<GadgetWithSnapPoint>() != null)
                    .OrderBy(spec => spec.ItemPrefabName, StringComparer.Ordinal)
                    .FirstOrDefault() ?? throw new InvalidOperationException("no hanger installs without a mount");
                NetworkedItem hangerItem = null;
                yield return Gadgets.SpawnOwned(hangerSpec, client.WorldPosition + Vector3.right * 1.5f + Vector3.up, client, item => hangerItem = item);
                GadgetBase installed = Gadgets.Install(hangerItem, car.Customization, car.interior.TransformPoint(Vector3.up * 1.5f));
                for (int i = 0; i < 60; i++) yield return null;
                SnapPointGadget point = installed.GetComponent<GadgetWithSnapPoint>().snapPoint;
                InventoryItemSpec hangs = DV.Globals.G.Items.items
                    .Where(spec => spec != null && spec.GetComponent<GadgetItem>() == null &&
                                   spec.GetComponentInChildren<ItemMagazine>(true) == null &&
                                   spec.GetComponent<DV.CabControls.Spec.Item>() is { } snapping &&
                                   ((int)snapping.allowedSnapPointTypes & (int)point.SnapPointType) != 0)
                    .OrderBy(spec => spec.ItemPrefabName, StringComparer.Ordinal)
                    .FirstOrDefault() ?? throw new InvalidOperationException("nothing hangs on " + hangerSpec.ItemPrefabName);
                GameObject go = UnityEngine.Object.Instantiate(hangs.gameObject, point.transform.position + Vector3.up * 0.2f, Quaternion.identity, WorldMover.OriginShiftParent);
                go.GetComponent<InventoryItemSpec>().BelongsToPlayer = true;
                NetworkedItem hung = go.GetComponent<NetworkedItem>();
                yield return Wait.Until(() => hung != null && hung.NetId != 0, 5f, $"the {hangs.ItemPrefabName} to get a net ID");
                Items.Track(hung.NetId);
                for (int i = 0; i < 30; i++) yield return null;
                if (!point.SnapItem(hung.Item, false)) throw new Exception($"the hanger refused the {hangs.ItemPrefabName}");
                // A player's snap comes with the release that marks the item for the server's dirty
                // pass; this bare one doesn't. In a cab whose interior isn't loaded here (the host's
                // player far off) vanilla makes the hung item inactive, so no LateUpdate sees it either.
                hung.MarkLocalStateDirty("snapped");
                for (int i = 0; i < 120; i++) yield return null;
                step.Result = new Hung
                {
                    Item = hung.NetId, HangerNetId = hangerItem.NetId, Hanger = hangerSpec.ItemPrefabName,
                    Prefab = hangs.ItemPrefabName, State = hung.GetItemState().ToString()
                };
            }

            /// <summary>Host: whether the installed hanger of this gadget item has an item on it.</summary>
            [Step]
            public static bool HangerHolds(ushort hangerNetId) =>
                NetworkedItem.TryGet(hangerNetId, out NetworkedItem item) && item != null &&
                item.GetComponent<GadgetItem>()?.Gadget?.GetComponent<GadgetWithSnapPoint>()?.snapPoint?.SnappedItem != null;

            /// <summary>How many items this game has snapped to the car's couplers (-1 when its couplers
            /// aren't loaded within 20 s).</summary>
            [Step]
            public static IEnumerator OnCouplers(Step<int> step, string car)
            {
                TrainCar c = Trains.Find(car);
                float t0 = Time.realtimeSinceStartup;
                while ((c.physicsLod == null || !c.physicsLod.GetCouplerSnapPoints().Any()) && Time.realtimeSinceStartup - t0 < 20f)
                    yield return null;
                step.Result = c.physicsLod == null ? -1 : c.physicsLod.GetCouplerSnapPoints().Count(p => p.SnappedItem != null);
            }
        }
    }
}
