using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Client.Items.LostAndFound;
using Multiplayer.Client.Items.Requests;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Items;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Every route into a full inventory lands in a slot or falls back, never in no slot (OI-START-10;
    /// docs/design/items/inventory.md, "A full inventory"). The client has a mug of its own in its
    /// Lost and Found, another in a box of its own, and its essential dropped (its silhouette keeps
    /// the slot). Before the fill, a stand-in whose reserved claim the client's game refuses (its slot
    /// reserved by another) lands in a free slot, not in none. Then it fills every
    /// other slot, and into the full inventory:
    /// 1. The box's mug is withdrawn: it comes out into the client's Lost and Found (InventoryOverflow).
    /// 2. The Lost and Found mug is retrieved: refused (inventory-full), and still in Lost and Found;
    ///    or, if a slot was free after all, in that slot.
    /// 3. A mug of the client's is registered into the inventory as the server makes a cold row go
    ///    live: no slot free after all, so at the client's feet (World), never in no slot.
    ///    And an admin's /give: into the client's Lost and Found (InventoryOverflow), as a starting item.
    /// 4. The essential is recalled: into its own slot, which its silhouette kept.
    /// After each, every item the server has in the client's inventory is in one of its slots.
    /// </summary>
    [Scenario("full-inventory-routes", "A Lost and Found retrieval, a withdrawal and a recall into a full inventory each land in a slot or fall back (OI-START-10)")]
    [Budget(900)]
    public sealed class FullInventoryRoutes : Scenario<FullInventoryRoutes.Options>
    {
        public sealed class Options
        {
            /// <summary>The box's prefab.</summary>
            public string Container = "ItemContainerToolbox";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            List<string> ids = new();
            var spawned = new List<ushort>();
            pair.Cleanup(async () =>
            {
                var now = new List<ushort>(spawned);
                foreach (string id in ids)
                {
                    ushort netId = await pair.Host.Run(() => Owned.NetIdOf(id));
                    if (netId != 0) now.Add(netId);
                }
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, now.Distinct().ToArray()), 120);
            });

            // A mug of the client's own in its Lost and Found.
            ushort lostMug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(lostMug, "client"));
            string lostId = await pair.Host.Run(() => Owned.IdOf(lostMug));
            ids.Add(lostId);
            Require.That(await pair.Host.Run<bool>(s => Ownership.Collect(s, lostMug, "SaveRecovery")), "the client's mug went to its Lost and Found");
            ItemRecord lost = await pair.Host.Run(() => Items.Record(lostMug));
            Require.That(lost.Lost, "the mug is in Lost and Found", lost.ToString());

            // Another in a box of its own.
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Container, "client"));
            spawned.Add(box);
            ushort boxMug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(boxMug, "client"));
            string boxId = await pair.Host.Run(() => Owned.IdOf(boxMug));
            ids.Add(boxId);
            Require.Equal(Boxes.Accepted, await pair.Client.Run<string>(s => Boxes.Deposit(s, box, boxMug), 120), "the deposit of the client's mug");
            int boxSlot = await pair.Host.Run(() => Boxes.StoredSlot(boxId));
            Require.That(boxSlot >= 0, "the mug is in the box", $"slot {boxSlot}");

            // Its essential dropped: the silhouette keeps its slot.
            await Recall.EssentialToInventory(pair);
            Recall.Dropped d = await pair.Client.Run<Recall.Dropped>(s => Recall.InGame.DropEssential(s, -1));
            Report.Note($"the client dropped {d.Name} {d.Item} from slot {d.Slot}: {d.State}, slot reserved {d.Reserved}");
            Require.Equal("Dropped", d.State, "the essential's state after the drop");

            // A claim the client's game refuses (its slot reserved by another): in the first free slot, not in none.
            string refused = await pair.Client.Run(() => InGame.RefusedClaim());
            Report.Note("the refused claim: " + refused);
            Expect.That(refused.StartsWith("slot "), "a stand-in whose reserved claim is refused lands in a free slot", refused);

            // Every slot taken.
            int taken = (await pair.Client.Run(() => LeaveFullInventory.InGame.Occupied())).Length;
            await LeaveFullInventory.Fill(pair, ids, LeaveFullInventory.Capacity - taken, false);
            Require.Zero(LeaveFullInventory.Capacity - (await pair.Client.Run(() => LeaveFullInventory.InGame.Occupied())).Length, "free slots in the client's inventory");
            await NoneInNoSlot(pair, client, "with every slot taken");

            // 1. The withdrawal: out, into its Lost and Found.
            string withdrawn = await pair.Client.Run<string>(s => Boxes.Withdraw(s, box, boxSlot), 120);
            Report.Note("the withdrawal: " + withdrawn);
            Expect.That(withdrawn.StartsWith(Boxes.Accepted), "the withdrawal into a full inventory", withdrawn);
            await Task.Delay(2000);
            OwnerInfo fromBox = await pair.Host.Run(() => Ownership.ById(boxId));
            Report.Note("the box's mug after: " + fromBox);
            Expect.Equal("LostAndFound", fromBox.Placement, "the box's mug, withdrawn with no slot free");
            Expect.Equal("InventoryOverflow", await pair.Host.Run(() => SaveHomes.InGame.LostReason(boxId)), "why it is in Lost and Found");
            await NoneInNoSlot(pair, client, "after the withdrawal");

            // 2. The retrieval: refused, and still in Lost and Found (or in a slot).
            string retrieved = await pair.Client.Run<string>(s => InGame.TryRetrieve(s, lost.Handle), 60);
            Report.Note("the retrieval: " + retrieved);
            await Task.Delay(2000);
            OwnerInfo after = await pair.Host.Run(() => Ownership.ById(lostId));
            Report.Note("the Lost and Found mug after: " + after);
            if (retrieved.StartsWith("refused"))
            {
                Expect.That(retrieved.Contains("inventory-full"), "the retrieval into a full inventory is refused for having no slot", retrieved);
                Expect.That(after.Lost && after.Placement == "LostAndFound", "the refused mug is still in Lost and Found", after.ToString());
            }
            else
            {
                Expect.Equal("PlayerInventory", after.Placement, "the retrieved mug");
                Expect.That(!after.Lost, "the retrieved mug is no longer in Lost and Found");
            }
            await NoneInNoSlot(pair, client, "after the retrieval");

            // 3. An item the server registers into the full inventory (a cold row going live, a
            //    starting item): no slot free after all, so at the client's feet.
            ushort late = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(late, "client"));
            ids.Add(await pair.Host.Run(() => Owned.IdOf(late)));
            string registered = await pair.Host.Run(() => InGame.RegisterIntoInventory(late));
            Report.Note("registered into the full inventory: " + registered);
            Expect.Equal("World", registered, "an item registered into the full inventory, its placement");
            await NoneInNoSlot(pair, client, "after the registration");

            // An admin's /give into the full inventory: into the client's Lost and Found, as a starting item.
            string given = await pair.Host.Run<string>(s => InGame.Give(s, "Mug"), 60);
            Report.Note("the /give into the full inventory: " + given);
            string[] g = given.Split(' ');
            ids.Add(g[1]);
            Expect.Equal("LostAndFound", g[0], "a /give into the full inventory, its placement");
            Expect.Equal("InventoryOverflow", await pair.Host.Run(() => SaveHomes.InGame.LostReason(g[1])), "why the /give is in Lost and Found");
            await NoneInNoSlot(pair, client, "after the /give");

            // 4. The recall: into the slot its silhouette kept.
            await pair.Client.Run<Abs>(s => Walk.Away(s, 20, 0));
            Recall.Recalled r = await pair.Client.Run<Recall.Recalled>(s => Recall.InGame.Recall(s, d.Item, d.Slot));
            Report.Note($"the recall: slot {r.Slot}, {r.State} after {r.Seconds:0.#} s");
            Expect.Equal(d.Slot, r.Slot, "the slot the essential is back in");
            await Task.Delay(2000);   // the recall's record settles on the host
            ItemRecord h = await pair.Host.Run(() => Items.Record(d.Item));
            Report.Note($"host: {h}");
            Expect.That(h.Placement == "PlayerInventory" && h.PlacementPlayer == h.ClientId, "the host records the essential in the client's inventory",
                $"{h.Placement} of player {h.PlacementPlayer}, the client is {h.ClientId}");
            await NoneInNoSlot(pair, client, "after the recall");
        }

        private static async Task NoneInNoSlot(Pair pair, string client, string when)
        {
            ushort[] held = await pair.Host.Run(() => LeaveFullInventory.InGame.InInventoryOf(client));
            Expect.Empty(await pair.Client.Run(() => LeaveFullInventory.InGame.InNoSlot(held)),
                $"items the server has in the client's inventory in none of its slots, {when}");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Client: two stand-ins (net IDs no item has) claiming one free slot, both reserved: the
            /// first takes it, and the second's claim is refused, since the slot's holder reserves it
            /// itself. Result: "slot N", the second's slot, or "in no slot". Both go after.
            /// </summary>
            [Step]
            public static string RefusedClaim()
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                int free = Enumerable.Range(0, inv.Capacity).First(s => inv.PeekItemAtSlot(s, true) == null);
                ItemUpdate Row(ushort netId) => new ItemUpdate
                {
                    ItemNetId = netId,
                    PrefabName = "Mug",
                    InventorySlot = netId == 65530 ? free : -1,
                    InventoryClaimSlot = free,
                    InventoryClaimFlags = ItemInventoryClaimFlags.Reserved
                };
                var standIns = new List<InventoryStandIn>();
                try
                {
                    using IDisposable placement = InventoryIntegration.BeginAuthoritativePlacement();
                    foreach (ushort netId in new ushort[] { 65530, 65531 })
                    {
                        InventoryStandIn standIn = InventoryStandIn.For(netId, "Mug");
                        standIns.Add(standIn);
                        standIn.ClaimSlot = free;
                        standIn.ClaimFlags = ItemInventoryClaimFlags.Reserved;
                        standIn.Place(Row(netId));
                    }
                    if (inv.IndexOf(standIns[0].gameObject) != free)
                        throw new Exception($"the first stand-in isn't in its slot {free}");
                    int second = inv.IndexOf(standIns[1].gameObject);
                    return second >= 0 ? "slot " + second : "in no slot";
                }
                finally
                {
                    foreach (InventoryStandIn standIn in standIns)
                    {
                        VanillaInventorySlots.Forget(inv, standIn.gameObject);
                        standIn.Retire();
                    }
                }
            }

            /// <summary>
            /// Host: registers the item into the client's inventory as the server registers a cold row
            /// going live, in slot 0. Result: its placement after.
            /// </summary>
            [Step]
            public static string RegisterIntoInventory(ushort netId)
            {
                if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null)
                    throw new Exception("no item " + netId);
                Multiplayer.Server.Session.ServerPlayer holder = Gadgets.Client();
                ItemUpdate snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Create);
                snapshot.ItemState = ItemState.InInventory;
                snapshot.PlayerId = holder.PlayerId;
                snapshot.InventorySlot = 0;
                snapshot.InventoryClaimSlot = -1;
                Guid id = Guid.Parse(Owned.IdOf(netId));
                var record = Multiplayer.Server.Items.LiveState.LiveItems.RegisterMaterializedColdItem(item, holder, holder.Guid, snapshot, id);
                if (record == null)
                    throw new Exception("the registration failed");
                return record.Placement.ToString();
            }

            /// <summary>Host: an admin's /give of the prefab to the client. Result: the new item's placement and id.</summary>
            [Step]
            public static IEnumerator Give(Step<string> step, string prefab)
            {
                var before = new HashSet<ushort>(Multiplayer.Server.Items.LiveState.LiveItems.GetAll().Select(r => r.NetId));
                InventoryItemSpec spec = InventoryStandIn.FindSpec(prefab) ?? throw new Exception("no item " + prefab);
                yield return Multiplayer.Server.Chat.AdminItemGrants.Spawn(Gadgets.Client(), new[] { spec });
                var made = Multiplayer.Server.Items.LiveState.LiveItems.GetAll()
                    .FirstOrDefault(r => !before.Contains(r.NetId) && r.PrefabName == prefab)
                    ?? throw new Exception("the /give made no " + prefab);
                step.Result = $"{made.Placement} {made.PersistentItemId:D}";
            }

            /// <summary>
            /// Client: collects its Lost and Found row (by its handle) and waits for the result.
            /// Result: "accepted slot N", or "refused" and the refusal.
            /// </summary>
            [Step]
            public static IEnumerator TryRetrieve(Step<string> step, uint handle)
            {
                ItemRow entry = null;
                float t0 = Time.realtimeSinceStartup;
                while (entry == null && Time.realtimeSinceStartup - t0 < 30f)
                {
                    ItemClient.Current.RequestItemList(ItemListKind.LostAndFound);
                    for (int i = 0; i < 30; i++) yield return null;
                    entry = LostAndFoundList.Items.FirstOrDefault(e => e.Handle == handle);
                }
                if (entry == null) throw new Exception("row " + handle + " never appeared in this player's Lost and Found list");
                ClientboundItemResultPacket result = null;
                uint requestId = 0;
                void Got(ClientboundItemResultPacket p) { if (p.RequestId == requestId) result = p; }
                ItemClient.Current.ItemResultReceived += Got;
                try
                {
                    requestId = ItemClient.Current.SendItemRequest(ItemExchange.CollectLost(entry));
                    t0 = Time.realtimeSinceStartup;
                    while (result == null && Time.realtimeSinceStartup - t0 < 15f) yield return null;
                }
                finally
                {
                    ItemClient.Current.ItemResultReceived -= Got;
                }
                if (result == null) throw new Exception("no retrieval result");
                if (!result.Accepted)
                {
                    step.Result = $"refused {result.Refusal} {result.Detail}";
                    yield break;
                }
                ushort id = result.ItemNetId;
                yield return Wait.Until(() => PlayerInventory.Holds(id), 15f, $"retrieved item {id} in the inventory");
                step.Result = "accepted slot " + PlayerInventory.Slot(id);
            }
        }
    }
}
