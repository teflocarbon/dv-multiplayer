using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Item bug SL4 (docs/research/item-bug-review.md): the client drops its comms radio in a moving
    /// loco's cab, where the item has a physics lease, and recalls it into its slot. Before R3 the
    /// recall committed the inventory placement without ending the lease, so the save wrote the
    /// lease's placement and after a restart the radio was back in the cab. Since R3 the lease is
    /// gone after the recall (likely because an item in a remote player's inventory is data on
    /// the server, OI-COLD-06, so its object goes with its lease). The radio doesn't always still have a lease when it's recalled (it may have settled):
    /// such a run checks only the recall and the restart, and says so.
    /// </summary>
    [Scenario("item-bug-recall-lease", "An essential recalled out of a moving cab has no physics lease left, and is in the inventory after a save and restart")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class ItemBugRecallLease : Scenario<ItemBugRecallLease.Options>
    {
        public sealed class Options
        {
            /// <summary>The essential's slot (0, the comms radio).</summary>
            public int Slot;
            /// <summary>m/s.</summary>
            public float Speed = 6;
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "host"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));
            Recall.Dropped d = await pair.Client.Run<Recall.Dropped>(s => Recall.InGame.DropEssential(s, o.Slot));
            Report.Note($"the client dropped {d.Name} {d.Item} from slot {o.Slot}");
            Running<object> push = pair.Host.Start(() => Cars.Push(loco, o.Speed, 20f, 3f), 120);
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, d.Item, "drop", 30), 90);
            bool leased = await pair.Host.Run(() => InGame.Leased(d.Item));
            Report.Note(leased ? "in the moving cab: the host has a physics lease on it"
                : "in the moving cab: no physics lease on it (it settled), so this run doesn't test SL4's lease");

            Recall.Recalled r = await pair.Client.Run<Recall.Recalled>(s => Recall.InGame.Recall(s, d.Item, o.Slot));
            Report.Note($"recalled: slot {r.Slot}, {r.State} after {r.Seconds:0.#} s");
            Require.Equal(o.Slot, r.Slot, "the slot the radio is back in");
            await push;
            await Task.Delay(10000);
            ItemRecord record = await pair.Host.Run(() => Items.Record(d.Item));
            bool stillLeased = await pair.Host.Run(() => InGame.Leased(d.Item));
            Report.Note($"10 s after the recall: {record.Placement} of {record.PlacementPlayer}, lease {stillLeased}");
            Expect.That(!stillLeased, "no physics lease left on the recalled radio");

            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart(o.Session);
            await Task.Delay(12000);
            string where = await pair.Client.Run(() => InGame.Radio());
            Report.Note("after the restart: " + where);
            pair.Cleanup(() => pair.Client.Run(() => ItemBugDroppedEssentialRejoin.InGame.FetchByName(d.Name, o.Slot), 90));
            Expect.That(where.StartsWith("slot "), $"the {d.Name} is in the client's inventory after the restart", where);
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the item has a live physics lease.</summary>
            [Step]
            public static bool Leased(ushort id) => NetworkedItemManager.Instance.HasActiveSpatialState(id);

            /// <summary>Client: where its comms radio is.</summary>
            [Step]
            public static string Radio()
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                byte me = Multiplayer.Shared.Session.NetworkLifecycle.Instance.LocalSession.PlayerId;
                var radio = Dev.All<NetworkedItem>().FirstOrDefault(n => n != null && n.NetId != 0 && n.Relation == ItemRelation.Mine && n.name.StartsWith("CommsRadio"));
                if (radio == null) return "the client owns no comms radio";
                int slot = inv.IndexOf(radio.gameObject);
                return slot >= 0 && radio.GetItemState() == ItemState.InInventory ? $"slot {slot}"
                    : $"{radio.GetItemState()}{(slot >= 0 ? $" (silhouette in slot {slot})" : "")}, in a car: {TrainCar.Resolve(radio.transform)?.ID ?? "no"}, at {Dev.Abs(radio.transform.position)}";
            }
        }
    }
}
