using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Client.Items;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Essential item recall: the client drops an essential item (its slot keeps a reserved
    /// silhouette), walks away and recalls it into that slot. With TAKEN=1 the host picks the item
    /// up first, so the recall has to take it from another player's hands (a player host only).
    /// </summary>
    [Scenario("recall", "A dropped essential item is recalled into its reserved slot (TAKEN=1: out of the host's hands)")]
    public sealed class Recall : Scenario<Recall.Options>
    {
        public sealed class Options
        {
            /// <summary>The essential item's inventory slot; -1 (the default) is the first slot holding an
            /// essential. The server puts each essential in a free slot (owned-items.md, PR 4), so slot 0
            /// needn't be the comms radio.</summary>
            public int Slot = -1;
            /// <summary>The host picks the item up before the recall.</summary>
            public bool Taken;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Taken && pair.Dedicated) throw new HarnessException("TAKEN=1 needs a player host: a dedicated host has no hands to take the item");
            if (o.Slot < 0)
                await EssentialToInventory(pair);
            Dropped d = await pair.Client.Run<Dropped>(s => InGame.DropEssential(s, o.Slot));
            Report.Note($"the client dropped {d.Name} {d.Item} from slot {d.Slot}: {d.State}, slot reserved {d.Reserved}");
            Require.Equal("Dropped", d.State, "the essential item's state after the drop");
            if (o.Taken)
                Require.Equal(d.Item, await pair.Host.Run<ushort>(s => InGame.Take(s, d.Item)), "the item the host holds");

            await pair.Client.Run<Abs>(s => Walk.Away(s, 40, 0));
            Recalled r = await pair.Client.Run<Recalled>(s => InGame.Recall(s, d.Item, d.Slot));
            Report.Note($"client: slot {r.Slot}, {r.State} after {r.Seconds:0.#} s");
            Expect.Equal(d.Slot, r.Slot, "the slot the item is back in");
            await Task.Delay(2000);   // the recall's record settles on the host
            ItemRecord h = await pair.Host.Run(() => Items.Record(d.Item));
            Report.Note($"host: {h}");
            Expect.That(h.Placement == "PlayerInventory" && h.PlacementPlayer == h.ClientId, "the host records it in the client's inventory", $"{h.Placement} of player {h.PlacementPlayer}, the client is {h.ClientId}");
            if (o.Taken)
                Expect.That(await pair.Host.Run(() => Items.Held()) != d.Item, "the host no longer holds the recalled item");
        }

        // Earlier runs in the same save can leave the client's essentials dropped or in its Lost
        // and Found (lost-and-found and starting-items do): bring one back first, as its player would.
        private static async Task EssentialToInventory(Pair pair)
        {
            Essential e = await pair.Client.Run<Essential>(s => InGame.FindEssential(s));
            if (e.Where == "inventory")
                return;
            Report.Note($"the client's {e.Name} {e.Item} is in {e.Where}: bringing it back to the inventory first");
            if (e.Where == "Lost and Found")
                await pair.Client.Run<int>(s => LostAndFound.InGame.Retrieve(s, e.Item), 60);
            else
                Require.Equal(e.Slot, (await pair.Client.Run<Recalled>(s => InGame.Recall(s, e.Item, e.Slot))).Slot,
                    "the slot the essential left in the world was recalled into");
        }

        public sealed class Essential
        {
            public ushort Item;
            public int Slot;
            public string Name, Where;
        }

        public sealed class Dropped
        {
            public ushort Item;
            public int Slot;
            public string Name, State;
            public bool Reserved;
        }

        public sealed class Recalled
        {
            public int Slot;
            public string State;
            public float Seconds;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Client: one of this player's essential items, wherever it is: in the
            /// inventory, else in their Lost and Found list, else lying in the world with its slot
            /// reserved.</summary>
            [Step]
            public static IEnumerator FindEssential(Step<Essential> step)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                for (int i = 0; i < inv.Capacity; i++)
                {
                    GameObject go = inv.PeekItemAtSlot(i, false);
                    if (go?.GetComponent<InventoryItemSpec>()?.IsEssential != true) continue;
                    step.Result = new Essential { Item = go.GetComponent<NetworkedItem>().NetId, Slot = i, Name = go.name.Replace("(Clone)", ""), Where = "inventory" };
                    yield break;
                }

                LocalSessionCalls.Call("RequestLostItems", 1u);
                for (int i = 0; i < 60; i++) yield return null;
                var essentials = DV.Globals.G.Items.items.Where(s => s != null && s.IsEssential).Select(s => s.ItemPrefabName).ToList();
                LostItemData lost = LostAndFoundList.Items.FirstOrDefault(e => essentials.Contains(e.PrefabName));
                if (lost != null)
                {
                    step.Result = new Essential { Item = lost.NetId, Slot = -1, Name = lost.PrefabName, Where = "Lost and Found" };
                    yield break;
                }

                byte self = NetworkRole.LocalPlayerId;
                NetworkedItem dropped = Dev.All<NetworkedItem>().FirstOrDefault(n => n != null && n.NetId != 0 &&
                    n.PersistentOwnerPlayerId == self && n.Item?.InventorySpecs?.IsEssential == true &&
                    n.GetItemState() == ItemState.Dropped && n.InventoryClaimSlot >= 0);
                if (dropped == null) throw new Exception("this player has no essential item in the inventory, Lost and Found or the world");
                step.Result = new Essential { Item = dropped.NetId, Slot = dropped.InventoryClaimSlot, Name = dropped.name.Replace("(Clone)", ""), Where = "the world" };
            }

            /// <summary>Client: drops the essential item in the slot into the world (as dropping it
            /// from the inventory does), leaving its reserved silhouette behind.</summary>
            [Step]
            public static IEnumerator DropEssential(Step<Dropped> step, int slot)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                if (slot < 0)
                    for (int i = 0; i < inv.Capacity && slot < 0; i++)
                        if (inv.PeekItemAtSlot(i, false)?.GetComponent<InventoryItemSpec>()?.IsEssential == true) slot = i;
                if (slot < 0) throw new Exception("no essential in the inventory");
                var go = inv.PeekItemAtSlot(slot, false) ?? throw new Exception("slot " + slot + " is empty");
                var n = go.GetComponent<NetworkedItem>();
                inv.DropItemFromHandsOrInventory(slot);
                for (int i = 0; i < 90; i++) yield return null;
                step.Result = new Dropped { Slot = slot, Item = n.NetId, Name = go.name.Replace("(Clone)", ""), State = n.GetItemState().ToString(), Reserved = inv.GetSlotReservedState(slot) };
            }

            /// <summary>Host: picks up another player's dropped item. Result: what it holds then.</summary>
            [Step]
            public static IEnumerator Take(Step<ushort> step, ushort id)
            {
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                for (int i = 0; i < 60; i++) yield return null;
                Hands.ReleaseControl();
                step.Result = Hands.Held?.NetId ?? 0;
            }

            /// <summary>Client: recalls an essential item into its reserved slot (what clicking the
            /// silhouette does) and waits up to 10 s for it to arrive. Result: the slot it's in, -1
            /// if it isn't back.</summary>
            [Step]
            public static IEnumerator Recall(Step<Recalled> step, ushort id, int slot)
            {
                if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null) throw new Exception("no item " + id);
                NetworkedItemManager.Instance.RequestItemRecall(item, slot);
                var inv = DV.InventorySystem.Inventory.Instance;
                float t0 = Time.realtimeSinceStartup;
                bool Back() => inv.PeekItemAtSlot(slot, false) == item.gameObject && item.GetItemState() == ItemState.InInventory;
                while (Time.realtimeSinceStartup - t0 < 10f && !Back()) yield return null;
                step.Result = new Recalled { Slot = Back() ? slot : -1, State = item.GetItemState().ToString(), Seconds = Time.realtimeSinceStartup - t0 };
            }
        }
    }
}
