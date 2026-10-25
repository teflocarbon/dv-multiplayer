using System;
using System.Collections;
using System.Threading.Tasks;
using DV.Customization.Gadgets.Implementations;
using DV.Items.Snapping;
using Dvmp.Lib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// An item on a hanger across a save and restart (R8; docs/design/items/live-state.md, "Attached
    /// and snapped items"). A hanger of the client's (a gadget with a snap point) is installed in a
    /// loco's cab and an item of the client's hung on it (attached-offline's <c>Hang</c>). On the host
    /// its record is <c>SnappedAttachment</c> on the hanger's snap point, its ledger home
    /// <c>Snapped(gadget:&lt;hanger's id&gt;)</c>, and the client has it snapped to the hanger. The host
    /// saves and both games restart: the item, found by its id, is snapped to the hanger again (the
    /// load finds the hanger by its id, waiting up to 30 s for it), not in Lost and Found, its home and
    /// record unchanged, in both games.
    /// Before the fix a gadget snap was recorded <c>Attached</c> with car 0: the first check fails (the
    /// record is <c>Attached</c>, the home <c>Attached</c> with no car), and after the restart the load
    /// sent the item to its owner's Lost and Found (<c>SaveRecovery</c>), so the hanger was empty.
    /// The client does the player things, so it runs on a player host and a dedicated one.
    /// </summary>
    [Scenario("snapped-restart", "An item on a hanger in a loco's cab across a save and restart: snapped to the hanger again in both games, its home Snapped(gadget:<id>), not in Lost and Found")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class SnappedRestart : Scenario<SnappedRestart.Options>
    {
        public sealed class Options
        {
            /// <summary>The loco the hanger is installed in.</summary>
            public string Loco = "LocoDE2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            await KeepUncollected(pair);
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, o.Loco, "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));

            AttachedOffline.Hung hung = await pair.Host.Run<AttachedOffline.Hung>(s => AttachedOffline.InGame.Hang(s, loco), 120);
            string itemId = await pair.Host.Run(() => Owned.IdOf(hung.Item));
            string hangerId = await pair.Host.Run(() => Owned.IdOf(hung.HangerNetId));
            // The item off the hanger first, then the hanger, then (pushed earlier) the loco.
            pair.Cleanup(() => pair.Host.Run(() => Gadgets.DestroyByPersistentId(new[] { itemId, hangerId })));
            Report.Note($"on the hanger {hung.Hanger} ({hung.HangerNetId}, {hangerId}) in {loco}: a {hung.Prefab} ({hung.Item}, {itemId}), {hung.State}");
            Require.That(itemId.Length > 0 && hangerId.Length > 0, "the item and the hanger have ids", $"item '{itemId}', hanger '{hangerId}'");
            OwnerInfo given = await pair.Host.Run(() => Ownership.Give(hung.Item, "client"));
            Require.Equal(client, given.Owner, "the hung item's owner (the client)");
            string key = "gadget:" + hangerId;

            Snapped before = await Until.Value(() => pair.Host.Run(() => InGame.Read(itemId, hangerId)),
                s => s.Placement == "SnappedAttachment" && s.Home == "Snapped", 20, "the host has the item snapped to the hanger");
            Report.Note("before the restart: " + before);
            Expect.Equal("SnapPoint", before.ParentKind, "before the restart: what the item's record is on");
            Expect.Equal((int)hung.HangerNetId, before.ParentNetId, "before the restart: the gadget item its record names");
            Expect.Equal(key, before.ParentKey, "before the restart: the snap point's key in its record");
            Expect.Equal(key, before.Anchor, "before the restart: its ledger home's anchor");
            Expect.That(before.Holds, "before the restart: the hanger holds it on the host");
            Expect.Equal("", await pair.Client.Run<string>(s => InGame.OnHanger(s, hung.Item, hung.HangerNetId, 20f), 40),
                "before the restart: the client has it snapped to the hanger");

            await Task.Delay(3000);
            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart();
            await Task.Delay(12000);   // the client receives its items
            await KeepUncollected(pair);

            // The load waits up to 30 s for the hanger before giving up on it.
            Snapped after = await Until.Value(() => pair.Host.Run(() => InGame.Read(itemId, hangerId)),
                s => s.Lost || (s.Placement == "SnappedAttachment" && s.Holds), 40, "after the restart: the host has the item on the hanger, or in Lost and Found");
            Report.Note("after the restart: " + after);
            Require.That(after.Item != 0 && after.Hanger != 0, "after the restart: the item and the hanger are there, with their ids", after.ToString());
            Expect.That(!after.Lost, "after the restart: the item is not in Lost and Found", after.ToString());
            Expect.Equal("Snapped", after.Home, "after the restart: its ledger home's kind");
            Expect.Equal(key, after.Anchor, "after the restart: its ledger home's anchor");
            Expect.Equal("SnappedAttachment", after.Placement, "after the restart: its record's placement");
            Expect.Equal("SnapPoint", after.ParentKind, "after the restart: what its record is on");
            Expect.Equal((int)after.Hanger, after.ParentNetId, "after the restart: the gadget item its record names (its new NetId)");
            Expect.Equal(key, after.ParentKey, "after the restart: the snap point's key in its record");
            Expect.That(await pair.Host.Run(() => AttachedOffline.InGame.HangerHolds(after.Hanger)), "after the restart: the hanger holds an item on the host");
            Expect.That(after.Holds, "after the restart: the hanger holds this item on the host");
            Expect.Equal("", await pair.Client.Run<string>(s => InGame.OnHanger(s, after.Item, after.Hanger, 30f), 50),
                "after the restart: the client has it snapped to the hanger");
        }

        // Nothing goes to Lost and Found by the collection policy meanwhile, and after the restart resets it.
        private static async Task KeepUncollected(Pair pair)
        {
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 5000, Nearby = 5000, Grace = 900 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
        }

        /// <summary>The host's record and ledger home of the hung item, found by id (NetIds change at a restart).</summary>
        public sealed class Snapped
        {
            public ushort Item, Hanger;
            public string Placement = "", ParentKind = "", ParentKey = "", Home = "", Anchor = "";
            public int ParentNetId;
            /// <summary>In Lost and Found (bound there, or its ledger home).</summary>
            public bool Lost;
            /// <summary>The hanger's snap point holds this item.</summary>
            public bool Holds;

            public override string ToString() =>
                $"item {Item}: {Placement} on {ParentKind} {ParentNetId} '{ParentKey}', home {Home} '{Anchor}'" +
                $"{(Lost ? ", in Lost and Found" : "")}; hanger {Hanger} {(Holds ? "holds it" : "doesn't hold it")}";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the hung item's record and ledger home, and whether the hanger holds it.</summary>
            [Step]
            public static Snapped Read(string itemId, string hangerId)
            {
                var result = new Snapped
                {
                    Item = Owned.NetIdOf(itemId),
                    Hanger = Owned.NetIdOf(hangerId)
                };
                Guid id = Guid.Parse(itemId);
                if (ServerLedger.Ledger.TryGetHome(id, out Home home))
                {
                    result.Home = home.Kind.ToString();
                    result.Anchor = home.Anchor ?? "";
                    result.Lost = home.Kind == HomeKind.LostAndFound;
                }
                if (result.Item == 0 || !LiveItems.TryGet(result.Item, out LiveItems.LiveItem record))
                    return result;
                result.Lost |= LostAndFoundServer.Contains(result.Item);
                result.Placement = record.Placement.ToString();
                result.ParentKind = record.WorldParentKind.ToString();
                result.ParentNetId = record.WorldParentNetId;
                result.ParentKey = record.WorldParentKey ?? "";
                result.Holds = Why(result.Item, result.Hanger).Length == 0;
                return result;
            }

            /// <summary>
            /// Either game: waits up to <paramref name="seconds"/> for the item to be snapped to the
            /// installed hanger of this gadget item, both ways (the item's snapped-to point is the
            /// hanger's, and the hanger's snapped item is it). Result: "" once it is, else what's wrong.
            /// </summary>
            [Step]
            public static IEnumerator OnHanger(Step<string> step, ushort item, ushort hanger, float seconds)
            {
                float until = Time.realtimeSinceStartup + seconds;
                string why;
                while ((why = Why(item, hanger)).Length > 0 && Time.realtimeSinceStartup < until)
                    yield return new WaitForSecondsRealtime(0.5f);
                step.Result = why;
            }

            private static string Why(ushort item, ushort hanger)
            {
                if (!NetworkedItem.TryGet(item, out NetworkedItem n) || n == null)
                    return "no item " + item;
                SnapPointGadget point = GadgetSnapPoints.PointOf(hanger);
                if (point == null)
                    return "no installed hanger for gadget item " + hanger;
                ItemSnapPointBase on = n.Item?.SnappableItem?.SnappedTo;
                if (on != point)
                    return on == null ? $"not snapped ({n.GetItemState()})" : "snapped to " + on.name;
                if (point.SnappedItem != n.Item)
                    return "the hanger holds " + (point.SnappedItem != null ? point.SnappedItem.name : "nothing");
                return "";
            }
        }
    }
}
