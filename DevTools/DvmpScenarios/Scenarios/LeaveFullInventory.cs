using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Inventory;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Newtonsoft.Json.Linq;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Leaving and joining with no free slot (docs/design/items/inventory.md, "Cold inventories").
    /// The client's inventory is filled with mugs, then:
    /// 1. Every slot off the belt is taken and its compass is gone (OI-ESS-03): at the rejoin the new
    ///    compass goes to its Lost and Found (InventoryOverflow), not onto the belt, which stays empty.
    /// 2. The belt filled too, one of the mugs nobody's: the client leaves holding a lantern of its own
    ///    (OI-JOIN-06): with no free slot it goes to its Lost and Found (InventoryOverflow). The
    ///    unowned mug is a cold row in its slot and comes back there at the rejoin (OI-JOIN-07).
    /// 3. It leaves holding a mug nobody owns (OI-JOIN-06): it drops at the client's feet, no row.
    /// 4. It leaves with an absent player's mug in a slot (OI-JOIN-05): it goes to its owner's Lost and
    ///    Found (BorrowerLeft), is no row of the client's, and the next save has it there, not with them.
    /// 5. While it's away its inventory is full again and one cold mug of its own loses its slot, as
    ///    an identity merge leaves one whose slot the other identity holds (OI-ID-19; no other game
    ///    path leaves a cold row with no slot: every slot route checks the ledger, live unowned items
    ///    and cold rows, and a load sends a clash to Lost and Found at once). At the rejoin it has no
    ///    slot and goes to its Lost and Found by way of the client's feet (PlayerItems.GoLive,
    ///    InventoryOverflow, OI-START-09); the rest come back, none in no slot.
    /// </summary>
    [Scenario("leave-full-inventory", "No free slot: an issued essential, a held owned or unowned item at a leave, a borrowed item, and a cold row with no slot at a join")]
    [Needs(Need.RestartsGames)]
    [Budget(1500)]
    public sealed class LeaveFullInventory : Scenario<LeaveFullInventory.Options>
    {
        public sealed class Options
        {
            /// <summary>The essential destroyed for the issuer to make again (OI-ESS-03).</summary>
            public string Essential = "Compass";
        }

        internal const int FirstBeltSlot = 33, Capacity = 36;

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            List<string> ids = new();
            string essentialId = "";
            pair.Cleanup(async () =>
            {
                var now = new List<ushort>();
                foreach (string id in ids)
                {
                    ushort netId = await pair.Host.Run(() => Owned.NetIdOf(id));
                    if (netId != 0) now.Add(netId);
                    else await pair.Host.Run(() => InGame.ForgetCold(id));
                }
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, now.ToArray()), 120);
                // The essential the issuer made goes back into the emptied inventory.
                ushort essential = essentialId.Length > 0 ? await pair.Host.Run(() => Owned.NetIdOf(essentialId)) : (ushort)0;
                if (essential != 0 && (await pair.Host.Run(() => Items.Record(essential))).Lost)
                {
                    uint handle = await pair.Host.Run(() => Owned.HandleOf(essential));
                    await pair.Client.Run<int>(s => LostAndFound.InGame.Retrieve(s, handle), 90);
                }
            });

            // 1. Every slot off the belt taken, no compass: the issued one goes to Lost and Found.
            var before = await pair.Client.Run(() => ItemBugFullInventoryEssential.InGame.Read(o.Essential));
            Require.That(before.Essential != 0, $"the client has a {o.Essential}");
            Require.Zero((await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { before.Essential }))).Length, $"the host destroyed the client's {o.Essential}");
            await Task.Delay(2000);
            int[] taken = await pair.Client.Run(() => InGame.Occupied());
            Require.Zero(taken.Count(s => s >= FirstBeltSlot), "items on the client's belt before it is filled");
            await Fill(pair, ids, FirstBeltSlot - taken.Length, false);
            taken = await pair.Client.Run(() => InGame.Occupied());
            Report.Note($"client: slots taken {taken.Length}, on the belt {taken.Count(s => s >= FirstBeltSlot)}");
            Require.Equal(FirstBeltSlot, taken.Count(s => s < FirstBeltSlot), "the client's slots off the belt taken");
            Require.Zero(taken.Count(s => s >= FirstBeltSlot), "the client's belt slots taken");
            await pair.Client.Quit();
            await Task.Delay(4000);
            await pair.Rejoin();
            await Task.Delay(15000);   // the client loads and the server issues the essential
            essentialId = await pair.Host.Run(() => InGame.OwnedOf(client, o.Essential));
            Expect.That(essentialId.Length > 0, $"the client owns a new {o.Essential}");
            if (essentialId.Length > 0)
            {
                LedgerEntry e = await pair.Host.Run(() => Dvmp.Scenarios.Owned.Entry(essentialId));
                Report.Note($"the new {o.Essential}: {e}");
                Expect.Equal("LostAndFound", e.Place, $"the new {o.Essential}'s home, with no slot free off the belt");
                Expect.Equal("InventoryOverflow", await pair.Host.Run(() => SaveHomes.InGame.LostReason(essentialId)), $"why the new {o.Essential} is in Lost and Found");
            }
            Expect.Zero((await pair.Client.Run(() => InGame.Occupied())).Count(s => s >= FirstBeltSlot), "the client's belt slots taken after the rejoin");

            // 2. The belt filled too, one mug nobody's; it leaves holding a lantern of its own.
            await Fill(pair, ids, Capacity - FirstBeltSlot - 1, false);
            string foundId = (await Fill(pair, ids, 1, true))[0];
            Require.Zero(Capacity - (await pair.Client.Run(() => InGame.Occupied())).Length, "free slots in the client's inventory");
            int foundSlot = await pair.Host.Run(() => InGame.Slot(foundId));
            ushort lantern = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Lantern", "client"));
            await pair.Host.Run(() => Ownership.Give(lantern, "client"));
            string lanternId = await pair.Host.Run(() => Owned.IdOf(lantern));
            ids.Add(lanternId);
            await pair.Client.Run<int>(s => Items.Stash(s, lantern, true), 60);
            await Leave(pair);
            OwnerInfo l = await pair.Host.Run(() => Ownership.ById(lanternId));
            Report.Note("the held lantern: " + l);
            Expect.Equal("LostAndFound", l.Placement, "the client's own lantern, held as it left with no free slot");
            Expect.Equal(client, l.Owner, "the lantern's owner");
            Expect.Equal("InventoryOverflow", await pair.Host.Run(() => SaveHomes.InGame.LostReason(lanternId)), "why the lantern is in Lost and Found");
            Expect.Equal("cold", await pair.Host.Run(() => ColdHomes.InGame.State(foundId)), "the unowned mug in a slot as the client left");
            Expect.Equal(foundSlot, await pair.Host.Run(() => ColdHomes.InGame.RowSlot(foundId)), "the unowned mug's cold row slot");
            await Rejoin(pair);
            await Back(pair, client, foundId, foundSlot, "the unowned mug, after the rejoin");

            // 3. It leaves holding a mug nobody owns: at its feet.
            ushort stray = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "client"));
            string strayId = await pair.Host.Run(() => Owned.IdOf(stray));
            ids.Add(strayId);
            await pair.Client.Run<int>(s => Items.Stash(s, stray, true), 60);
            await Leave(pair);
            OwnerInfo st = await pair.Host.Run(() => Ownership.ById(strayId));
            Report.Note("the held unowned mug: " + st);
            Expect.Equal("World", st.Placement, "the unowned mug held as the client left with no free slot");
            Expect.That(!st.Lost, "the unowned mug is in no Lost and Found");
            Expect.Equal("object", await pair.Host.Run(() => ColdHomes.InGame.State(strayId)), "the unowned mug has no cold row");
            await Rejoin(pair);

            // 4. A borrowed mug in a slot as it leaves: its owner's Lost and Found, and not saved with the client.
            string dropped = ids[0];
            ushort droppedNow = await pair.Host.Run(() => Owned.NetIdOf(dropped));
            int freed = await pair.Host.Run(() => InGame.Slot(dropped));
            await pair.Client.Run(() => Owned.DropFromInventory(droppedNow), 60);
            ushort borrowed = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            AbsentOwner.Absent absent = await pair.Host.Run(() => AbsentOwner.InGame.GiveToAbsent(borrowed));
            ids.Add(absent.Id);
            int borrowedSlot = await pair.Client.Run<int>(s => Items.Stash(s, borrowed, false), 60);
            Report.Note($"the absent player's mug in slot {borrowedSlot} (freed slot {freed})");
            await Leave(pair);
            OwnerInfo bo = await pair.Host.Run(() => Ownership.ById(absent.Id));
            Report.Note("the borrowed mug: " + bo);
            Expect.Equal("LostAndFound", bo.Placement, "the borrowed mug as the client left");
            Expect.Equal(absent.Owner, bo.Owner, "the borrowed mug's owner");
            Expect.Equal("BorrowerLeft", await pair.Host.Run(() => SaveHomes.InGame.LostReason(absent.Id)), "why the borrowed mug is in Lost and Found");
            Expect.That(await pair.Host.Run(() => ColdHomes.InGame.State(absent.Id)) != "cold", "the borrowed mug is no cold row of the client's");
            await pair.Host.Run(() => Saves.Manual());
            string saved = await pair.Host.Run(() => InGame.SavedRow(absent.Id));
            Report.Note("the borrowed mug's saved ledger row: " + saved);
            Expect.That(saved.Contains("LostAndFound"), "the save has the borrowed mug in Lost and Found", saved);
            Expect.That(!saved.Contains(client), "the save has the borrowed mug nowhere of the client's", saved);

            // 5. While it's away its inventory is full again, and one cold mug of its own has its slot
            //    taken from it, as an identity merge leaves one (OI-ID-19): at the join it has no slot.
            string fill = await pair.Host.Run<string>(s => InGame.ColdInto(s, client, borrowedSlot));
            ids.Add(fill);
            string unslotted = ids[1];
            Require.Equal("cold", await pair.Host.Run(() => ColdHomes.InGame.State(unslotted)), "the mug to unslot is a cold row");
            Expect.Equal(Capacity, await pair.Host.Run(() => InGame.ColdCount(client)), "the client's cold rows: one in every slot");
            Report.Note("unslotted: " + await pair.Host.Run(() => InGame.Unslot(unslotted)));
            await Rejoin(pair);
            LedgerEntry ue = await pair.Host.Run(() => Dvmp.Scenarios.Owned.Entry(unslotted));
            OwnerInfo u = await pair.Host.Run(() => Ownership.ById(unslotted));
            Report.Note($"the unslotted mug after the rejoin: {u}; ledger {ue}");
            Expect.Equal("LostAndFound", u.Placement, "the cold mug with no slot, after the join");
            Expect.Equal("LostAndFound", ue.Place, "its ledger home");
            Expect.Equal("InventoryOverflow", await pair.Host.Run(() => SaveHomes.InGame.LostReason(unslotted)), "why it is in Lost and Found");
            Expect.That(await pair.Host.Run(() => ColdHomes.InGame.State(fill)) != "cold", "the mug put cold into the freed slot went live");
            Expect.Equal(borrowedSlot, await pair.Host.Run(() => InGame.Slot(fill)), "and is in that slot");
            string[] noSlot = Array.Empty<string>();
            ushort[] held = await pair.Host.Run(() => InGame.InInventoryOf(client));
            Report.Note($"the server has {held.Length} items in the client's inventory");
            for (int i = 0; i < 30; i++)
            {
                noSlot = await pair.Client.Run(() => InGame.InNoSlot(held));
                if (noSlot.Length == 0) break;
                await Task.Delay(1000);
            }
            Expect.Empty(noSlot, "the client's items in no slot");
        }

        // Mugs into the client's inventory (unowned ones: nobody's). Result: their ids.
        internal static async Task<string[]> Fill(Pair pair, List<string> ids, int count, bool unowned)
        {
            if (count <= 0) return Array.Empty<string>();
            ushort[] mugs = await pair.Host.Run<ushort[]>(s => InGame.SpawnMugs(s, count, unowned), 180);
            string[] made = await pair.Host.Run(() => InGame.IdsOf(mugs));
            ids.AddRange(made);
            int stashed = await pair.Client.Run<int>(s => ItemBugFullInventoryEssential.InGame.StashAll(s, mugs), 900);
            Require.Equal(count, stashed, "mugs the client stashed");
            await Task.Delay(2000);   // the stashes reach the server
            return made;
        }

        private static async Task Leave(Pair pair)
        {
            await pair.Client.Quit();
            await Until.Value(() => pair.Host.Run(() => Ownership.RemotePlayers()), n => n == 0, 60, "the host to notice the client left");
            await Task.Delay(3000);   // its departure is handled
        }

        private static async Task Rejoin(Pair pair)
        {
            await pair.Rejoin();
            await Task.Delay(15000);   // the client receives its items
        }

        private static async Task Back(Pair pair, string client, string id, int slot, string what)
        {
            OwnerInfo now = await pair.Host.Run(() => Ownership.ById(id));
            Report.Note($"{what}: {now}");
            if (!Expect.That(now.Exists, what + ": there")) return;
            Expect.Equal(client, now.Holder, what + ": whose inventory");
            Expect.Equal(slot, (await pair.Host.Run(() => Ledger.Where(now.NetId))).Slot, what + ": the server's slot");
            Expect.Equal(slot, await pair.Client.Run(() => Ledger.LocalSlot(now.NetId)), what + ": the client's slot");
            Expect.That(!(await pair.Host.Run(() => Dvmp.Scenarios.Owned.Entry(id))).Known, what + ": still nobody's");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Client: the slots that hold an item.</summary>
            [Step]
            public static int[] Occupied()
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                return Enumerable.Range(0, inv.Capacity).Where(s => inv.PeekItemAtSlot(s, true) != null).ToArray();
            }

            /// <summary>Host: the items the server has in the holder's inventory.</summary>
            [Step]
            public static ushort[] InInventoryOf(string holder) =>
                Multiplayer.Server.Items.LiveState.LiveItems.GetAll()
                    .Where(r => r.Placement == Multiplayer.Shared.Protocol.Data.Items.ItemPlacementKind.PlayerInventory && r.Holder == Guid.Parse(holder))
                    .Select(r => r.NetId).ToArray();

            /// <summary>Client: those of the items that are in none of its slots.</summary>
            [Step]
            public static string[] InNoSlot(ushort[] netIds)
            {
                // A stored item is a stand-in with no object (D14).
                return netIds.Where(id => PlayerInventory.Slot(id) < 0)
                    .Select(id => Multiplayer.Shared.Items.LiveState.NetworkedItem.TryGet(id, out var n) && n != null ? $"{n.name} {id}" : $"no object {id}")
                    .ToArray();
            }

            /// <summary>Host: <paramref name="count"/> mugs beside the client, personal ones or nobody's. Result: their net IDs.</summary>
            [Step]
            public static IEnumerator SpawnMugs(Step<ushort[]> step, int count, bool unowned)
            {
                var made = new List<ushort>();
                for (int i = 0; i < count; i++)
                {
                    var one = new Step<ushort>();
                    yield return unowned ? Items.Spawn(one, "Mug", "client") : Items.SpawnPersonal(one, "Mug", "client");
                    made.Add(one.Result);
                }
                step.Result = made.ToArray();
            }

            [Step]
            public static string[] IdsOf(ushort[] netIds) => netIds.Select(Dvmp.Scenarios.Owned.IdOf).ToArray();

            /// <summary>Host: the slot the server has the item in, or -1.</summary>
            [Step]
            public static int Slot(string id)
            {
                ushort netId = Dvmp.Scenarios.Owned.NetIdOf(id);
                return netId == 0 ? -1 : Ledger.Where(netId).Slot;
            }

            /// <summary>Host: the id of the live item of this prefab the player owns, or "".</summary>
            [Step]
            public static string OwnedOf(string owner, string prefab) =>
                ServerLedger.Ledger.LiveOwnedBy(OwnerRef.Player(Guid.Parse(owner)))
                    .FirstOrDefault(e => e.Prefab == prefab)?.Id.ToString("D") ?? "";

            /// <summary>Host: the saved <c>Multiplayer.Items</c> ledger row of the item, as text, or "".</summary>
            [Step]
            public static string SavedRow(string id) =>
                SaveEdits.LedgerRow(SaveEdits.Saved(), id)?.ToString(Newtonsoft.Json.Formatting.None) ?? "";

            /// <summary>
            /// Host: a new mug of the offline player's, put cold into their <paramref name="slot"/> by
            /// the one-home repair (D7) as if its ledger home were that slot (a free one: the ledger refuses a taken one).
            /// Result: its id.
            /// </summary>
            [Step]
            public static IEnumerator ColdInto(Step<string> step, string owner, int slot)
            {
                var one = new Step<ushort>();
                yield return Items.Spawn(one, "Mug", "host");
                Ownership.Give(one.Result, owner);
                string id = Dvmp.Scenarios.Owned.IdOf(one.Result);
                var home = new Step<string>();
                yield return LedgerRepair.InGame.Repair(home, id, "slot", owner, slot);
                if (!ColdInventories.Contains(Guid.Parse(id)))
                    throw new Exception($"the mug {id} isn't cold after the repair: the ledger has it {home.Result}");
                step.Result = id;
            }

            /// <summary>Host: the holder's cold rows.</summary>
            [Step]
            public static int ColdCount(string holder) => ColdInventories.Of(Guid.Parse(holder)).Count;

            /// <summary>
            /// Host: what an identity merge does to a cold row whose slot the other identity holds
            /// (<c>InstallGuidRekey</c>, OI-ID-19): the ledger sends it to Lost and Found and the row keeps
            /// no slot, to go there when its player joins. Result: the row.
            /// </summary>
            [Step]
            public static string Unslot(string id)
            {
                Guid item = Guid.Parse(id);
                if (!ColdInventories.TryGet(item, out var row))
                    throw new Exception($"{id} isn't cold");
                LedgerCommit moved = ServerLedger.Ledger.Repair(item, Home.LostAndFound,
                    new LedgerCause(ItemMoveReason.HostCorrection, Guid.Empty, "leave-full-inventory: its slot taken"));
                if (!moved.Committed)
                    throw new Exception($"the ledger refused {id} to Lost and Found: {moved.Refusal}");
                int was = row.Slot;
                row.Slot = -1;
                return $"{row.PrefabName} {id}, was in slot {was}";
            }

            /// <summary>Host, cleanup: an item left cold (its player never came back): its row and entry go.</summary>
            [Step]
            public static void ForgetCold(string id)
            {
                if (ColdInventories.Contains(Guid.Parse(id)))
                    LedgerRepair.InGame.Forget(id);
            }
        }
    }
}
