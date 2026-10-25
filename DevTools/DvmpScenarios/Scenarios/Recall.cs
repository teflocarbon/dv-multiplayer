using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Client.Items.LostAndFound;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Essential item recall: the client drops an essential item (its slot keeps a reserved
    /// silhouette), walks away and recalls it into that slot. With TAKEN=1 the host picks the item
    /// up first, so the recall has to take it from another player's hands (a player host only).
    /// STASHED=1 (with TAKEN): the host then puts it in its own inventory, and the client, its owner
    /// with its silhouette, must stop seeing it in the host's hand within 2 s (R4C C9 A5: the host's
    /// stash went to data in the same frame and was never sent); the recall then takes it from the
    /// host's inventory.
    /// </summary>
    [Scenario("recall", "A dropped essential item is recalled into its reserved slot (TAKEN=1: out of the host's hands; STASHED=1: out of its inventory)")]
    public sealed class Recall : Scenario<Recall.Options>
    {
        public sealed class Options
        {
            /// <summary>The essential item's inventory slot; -1 (the default) is the first slot holding an
            /// essential. The server puts each essential in a free slot (essentials.md, "The issuer"), so slot 0
            /// needn't be the comms radio.</summary>
            public int Slot = -1;
            /// <summary>The host picks the item up before the recall.</summary>
            public bool Taken;
            /// <summary>The host picks it up and puts it in its own inventory before the recall.</summary>
            public bool Stashed;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            o.Taken |= o.Stashed;
            if (o.Taken && pair.Dedicated) throw new HarnessException("TAKEN=1 needs a player host: a dedicated host has no hands to take the item");
            if (o.Slot < 0)
                await EssentialToInventory(pair);
            Dropped d = await pair.Client.Run<Dropped>(s => InGame.DropEssential(s, o.Slot));
            Report.Note($"the client dropped {d.Name} {d.Item} from slot {d.Slot}: {d.State}, slot reserved {d.Reserved}");
            Require.Equal("Dropped", d.State, "the essential item's state after the drop");
            if (o.Taken)
                Require.Equal(d.Item, await pair.Host.Run<ushort>(s => InGame.Take(s, d.Item)), "the item the host holds");
            if (o.Stashed)
            {
                Require.That(await pair.Client.Run<float>(s => InGame.InHostHand(s, d.Item, true, 5f)) >= 0, "the client sees its item in the host's hand");
                int hostSlot = await pair.Host.Run<int>(s => Items.Stash(s, d.Item, false), 60);
                Report.Note($"the host put it in its slot {hostSlot}");
                float gone = await pair.Client.Run<float>(s => InGame.InHostHand(s, d.Item, false, 2f));
                Expect.That(gone >= 0, "the client stops seeing its item in the host's hand within 2 s of the host's stash (A5)",
                    gone >= 0 ? $"after {gone:0.##} s" : "still in it");
            }

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
            if (o.Stashed)
                Expect.Equal(-1, await pair.Host.Run(() => Ledger.LocalSlot(d.Item)), "the host's slot with the recalled item");
        }

        // Earlier runs in the same save can leave the client's essentials dropped or in its Lost
        // and Found (lost-and-found and starting-items do): bring one back first, as its player would.
        internal static async Task EssentialToInventory(Pair pair)
        {
            Essential e = await pair.Client.Run<Essential>(s => InGame.FindEssential(s));
            if (e.Where == "inventory")
                return;
            Report.Note($"the client's {e.Name} {e.Item} is in {e.Where}: bringing it back to the inventory first");
            if (e.Where == "Lost and Found")
                await pair.Client.Run<int>(s => LostAndFound.InGame.Retrieve(s, e.Handle), 60);
            else
                Require.Equal(e.Slot, (await pair.Client.Run<Recalled>(s => InGame.Recall(s, e.Item, e.Slot))).Slot,
                    "the slot the essential left in the world was recalled into");
        }

        public sealed class Essential
        {
            public ushort Item;
            /// <summary>Its Lost and Found row's handle, when it's there (a row has no NetId).</summary>
            public uint Handle;
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
                foreach (var (netId, i, go) in PlayerInventory.Stored())
                {
                    if (!PlayerInventory.IsEssential(go)) continue;
                    step.Result = new Essential { Item = netId, Slot = i, Name = PlayerInventory.PrefabOf(go).Replace("(Clone)", ""), Where = "inventory" };
                    yield break;
                }

                ItemClient.Current.RequestItemList(ItemListKind.LostAndFound);
                for (int i = 0; i < 60; i++) yield return null;
                var essentials = DV.Globals.G.Items.items.Where(s => s != null && s.IsEssential).Select(s => s.ItemPrefabName).ToList();
                ItemRow lost = LostAndFoundList.Items.FirstOrDefault(e => essentials.Contains(e.PrefabName));
                if (lost != null)
                {
                    step.Result = new Essential { Handle = lost.Handle, Slot = -1, Name = lost.PrefabName, Where = "Lost and Found" };
                    yield break;
                }

                byte self = NetworkRole.LocalPlayerId;
                NetworkedItem dropped = Dev.All<NetworkedItem>().FirstOrDefault(n => n != null && n.NetId != 0 &&
                    n.Relation == ItemRelation.Mine && n.Item?.InventorySpecs?.IsEssential == true &&
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
                    slot = PlayerInventory.Stored().Where(e => PlayerInventory.IsEssential(e.go)).Select(e => e.slot).DefaultIfEmpty(-1).First();
                if (slot < 0) throw new Exception("no essential in the inventory");
                var go = inv.PeekItemAtSlot(slot, false) ?? throw new Exception("slot " + slot + " is empty");
                // On a client the slot holds a stand-in; the drop makes its object (D14).
                ushort id = PlayerInventory.NetIdOf(go);
                string name = PlayerInventory.PrefabOf(go).Replace("(Clone)", "");
                inv.DropItemFromHandsOrInventory(slot);
                for (int i = 0; i < 90; i++) yield return null;
                NetworkedItem.TryGet(id, out NetworkedItem n);
                step.Result = new Dropped { Slot = slot, Item = id, Name = name, State = n != null ? n.GetItemState().ToString() : "no object", Reserved = inv.GetSlotReservedState(slot) };
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

            /// <summary>Client: waits up to <paramref name="seconds"/> for this game's copy of the item to be
            /// (<paramref name="held"/>), or no longer be, in a player's hand here: an active object in
            /// state InHand. Result: the seconds it took, -1 if it didn't.</summary>
            [Step]
            public static IEnumerator InHostHand(Step<float> step, ushort id, bool held, float seconds)
            {
                bool InHand() => NetworkedItem.TryGet(id, out NetworkedItem item) && item != null &&
                                 item.gameObject.activeInHierarchy && item.GetItemState() == ItemState.InHand;
                float t0 = Time.realtimeSinceStartup;
                while (InHand() != held && Time.realtimeSinceStartup - t0 < seconds) yield return null;
                step.Result = InHand() == held ? Time.realtimeSinceStartup - t0 : -1f;
            }

            /// <summary>Client: recalls an essential item into its reserved slot (what clicking the
            /// silhouette does) and waits up to 10 s for it to arrive. Result: the slot it's in, -1
            /// if it isn't back.</summary>
            [Step]
            public static IEnumerator Recall(Step<Recalled> step, ushort id, int slot)
            {
                // In Lost and Found it has no object here, only its silhouette's stand-in (D14).
                if (PlayerInventory.SlotObject(id) == null) throw new Exception("no item " + id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                PlayerInventory.Recall(id, slot);
                var inv = DV.InventorySystem.Inventory.Instance;
                float t0 = Time.realtimeSinceStartup;
                // Back on a client, it's a stand-in and the object is gone (D14).
                bool Back() => PlayerInventory.Holds(id) && PlayerInventory.Slot(id) == slot &&
                               (PlayerInventory.IsStandIn(id) || item != null && item.GetItemState() == ItemState.InInventory);
                while (Time.realtimeSinceStartup - t0 < 10f && !Back()) yield return null;
                string state = PlayerInventory.IsStandIn(id) ? "InInventory (stand-in)" : item != null ? item.GetItemState().ToString() : "no object";
                step.Result = new Recalled { Slot = Back() ? slot : -1, State = state, Seconds = Time.realtimeSinceStartup - t0 };
            }
        }
    }
}
