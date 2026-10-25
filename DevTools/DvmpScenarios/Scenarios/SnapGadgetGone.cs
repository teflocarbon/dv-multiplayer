using System;
using System.Threading.Tasks;
using DV.Utils;
using Dvmp.Lib;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A saved item snapped to a gadget that never loads (docs/design/items/live-state.md, "Attached
    /// and snapped items"): after the load's 30 s wait (<c>ItemSaveLoad.SnapWhenItsGadgetIs</c>) an
    /// owned one goes to its owner's Lost and Found and an unowned one into the world where it is,
    /// never left snapped to nothing. The run makes the load's case on the host: a mug's record and
    /// ledger home are made <c>SnappedAttachment</c> on a gadget no game has (<c>gadget:&lt;new
    /// id&gt;</c>), as a load leaves an item whose gadget hasn't come, and the load's wait is started
    /// for it. Before the fix an unowned one stayed <c>SnappedAttachment</c> for good (the wait sent
    /// only an owned one anywhere). OWNED=1: the client's mug, which must go to its Lost and Found: a
    /// regression guard only, since an owned one went there before the fix too. That a refused one
    /// goes into the world once, not twice, is the Core rule <c>LostItemCollectionPolicy.RolledBackIntoWorld</c>'s.
    /// Either kind of host.
    /// </summary>
    [Scenario("snap-gadget-gone", "A saved item snapped to a gadget that never loads: unowned, into the world after the wait; OWNED=1, to its owner's Lost and Found")]
    [Budget(180)]
    public sealed class SnapGadgetGone : Scenario<SnapGadgetGone.Options>
    {
        public sealed class Options
        {
            /// <summary>The item's prefab.</summary>
            public string Prefab = "Mug";
            /// <summary>The client's, not nobody's.</summary>
            public bool Owned;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort item = o.Owned
                ? await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"))
                : await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "client"));
            if (o.Owned) await pair.Host.Run(() => Ownership.Give(item, "client"));
            await Task.Delay(2000);
            string key = await pair.Host.Run(() => InGame.SnapToNothing(item));
            Report.Note($"{(o.Owned ? "the client's" : "an unowned")} {o.Prefab} {item}, snapped to {key}, which no game has");
            Require.Equal("SnappedAttachment", (await pair.Host.Run(() => Items.Record(item))).Placement, "the item's record before the wait");

            ItemRecord after = await Until.Value(() => pair.Host.Run(() => Items.Record(item)),
                r => r.Placement != "SnappedAttachment" || r.Lost, 50, "the item off the gadget that never came (the load waits 30 s)");
            Report.Note($"after the wait: {after}");
            if (o.Owned)
                Expect.That(after.Lost, "the client's item in its Lost and Found", after.ToString());
            else
            {
                Expect.Equal("World", after.Placement, "the unowned item's placement: in the world where it is");
                Expect.That(!after.Lost, "the unowned item isn't in Lost and Found", after.ToString());
            }
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: the item's record and ledger home made snapped to a gadget no game has, as a load
            /// leaves an item whose gadget hasn't come, and the load's wait for that gadget started.
            /// Result: the snap point's key.
            /// </summary>
            [Step]
            public static string SnapToNothing(ushort netId)
            {
                NetworkedItem item = Items.Find(netId);
                if (!LiveItems.TryGet(netId, out LiveItems.LiveItem record)) throw new Exception("no record of item " + netId);
                string key = "gadget:" + Guid.NewGuid().ToString("D");
                record.Placement = ItemPlacementKind.SnappedAttachment;
                record.WorldParentKind = ItemWorldParentKind.SnapPoint;
                record.WorldParentNetId = 0;
                record.WorldParentKey = key;
                if (ServerLedger.Ledger.TryGet(record.PersistentItemId, out _))
                    ServerLedger.Ledger.Repair(record.PersistentItemId, Home.Snapped(key),
                        new LedgerCause(ItemMoveReason.HostCorrection, Guid.Empty, "snap-gadget-gone scenario"));
                SingletonBehaviour<CoroutineManager>.Instance.StartCoroutine(ItemSaveLoad.SnapWhenItsGadgetIs(item, record));
                return key;
            }
        }
    }
}
