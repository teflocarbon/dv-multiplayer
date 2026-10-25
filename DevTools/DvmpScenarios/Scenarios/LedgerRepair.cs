using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The one-home repair a Release build makes (D7, OI-LED-15; docs/design/items/ledger.md): an item
    /// whose ledger home isn't where it is goes to its ledger home, else to its owner's Lost and Found.
    /// A Debug build only reports a breach, so the run breaks the ledger on purpose and calls the
    /// repair (<c>ServerLedger.PutHome</c>) itself:
    /// - lying in the world, home a connected player's slot: into that slot, on both games;
    /// - in the client's inventory, home Lost and Found: collected there;
    /// - in Lost and Found, home the client's slot: recalled into it;
    /// - in the client's inventory, home the ground (no pose kept): the owner's Lost and Found;
    /// - lying in the world, home an offline player's slot: a cold row there, its object gone;
    /// - on a player host, lying in the world, home the host's own player's slot: into it; and from
    ///   there, home the client's slot: out of the host's inventory into the client's.
    /// The one-home check holds after each.
    ///
    /// MODE=strays: the repairs of what isn't the ledger's item in the ledger's home (R8,
    /// ServerLedger.Strays.cs). Each case breaks the world on purpose, has the Core check see the
    /// break, and calls <c>ServerLedger.Repair</c> with the violation as <c>CheckInvariant</c> builds it:
    /// - on a coupler, home the client's slot (LiveAgrees): taken off, into the slot (or its Lost and
    ///   Found); on a hanger, home Lost and Found: collected, the hanger free;
    /// - a live entry with no copy (OneHome): made again in its owner's Lost and Found, same id;
    /// - two mugs with one id (OneHome): the first keeps it; the second is re-minted, a Duplicate of
    ///   it, into its owner's Lost and Found;
    /// - an ended entry's mug still there (NoStray): re-minted, a Duplicate of it, into Lost and Found;
    /// - a mug in Lost and Found, its home, and a mug in the world with its id (OneHome, two copies):
    ///   the one in Lost and Found keeps it, the other is re-minted into Lost and Found (not made
    ///   again, which put the copy in Lost and Found unminted);
    /// - a mug in the world, its home, and a mug in Lost and Found with its id: the one in Lost and
    ///   Found is re-minted, and its entry is found under the new id, none under the old;
    /// - on a player host, an unowned mug's record in the slot the host's owned mug has
    ///   (OneItemPerSlot): the owned one stays, the unowned one is dropped into the world;
    /// - a mug in the world in vanilla's Lost and Found list too (VanillaAgrees): out of that list.
    /// The one-home check holds after each.
    /// </summary>
    [Scenario("ledger-repair", "The one-home repair (D7): an item outside its ledger home is put there, else in its owner's Lost and Found (MODE=strays: copies, strays, slot clashes and vanilla's lists)")]
    [Budget(480)]
    public sealed class LedgerRepair : Scenario<LedgerRepair.Options>
    {
        public sealed class Options
        {
            /// <summary>put-home (homes put right), or strays (ServerLedger.Strays.cs's repairs).</summary>
            public string Mode = "put-home";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Mode == "strays")
            {
                await Strays(pair);
                return;
            }
            if (o.Mode != "put-home") throw new ArgumentException("MODE: put-home or strays, not " + o.Mode);
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(mug, "client"));
            string mugId = await pair.Host.Run(() => Owned.IdOf(mug));
            await Task.Delay(2000);   // settled on the ground

            // In the world, home the client's slot: put there.
            int slot = await pair.Host.Run(() => InGame.FreeSlot(client, mugId));
            await Repaired(pair, mugId, "slot", client, slot, $"slot {slot} of {client}", "the mug on the ground, home the client's slot");
            await InSlot(pair, pair.Client, mug, slot, "the mug put in the client's slot");

            // In the client's inventory, home Lost and Found: collected.
            await Task.Delay(3000);   // data only on the server: the repair makes it again first
            await Repaired(pair, mugId, "lost", "", -1, "LostAndFound", "the mug in the client's inventory, home Lost and Found");
            Expect.Equal("SaveRecovery", await pair.Host.Run(() => LedgerRows.InGame.LostReason(client, mugId)), "why Lost and Found has it");

            // In Lost and Found, home the client's slot: recalled.
            mug = await pair.Host.Run(() => Owned.NetIdOf(mugId));
            slot = await pair.Host.Run(() => InGame.FreeSlot(client, mugId));
            await Repaired(pair, mugId, "slot", client, slot, $"slot {slot} of {client}", "the mug in Lost and Found, home the client's slot");
            mug = await pair.Host.Run(() => Owned.NetIdOf(mugId));
            await pair.Host.Run(() => Ledger.Track(mug));
            await InSlot(pair, pair.Client, mug, slot, "the mug recalled into the client's slot");

            // In the client's inventory, home the ground: the ledger keeps no pose, so Lost and Found.
            await Task.Delay(3000);
            await Repaired(pair, mugId, "world", "", -1, "LostAndFound", "the mug in the client's inventory, home the ground");

            // Lying in the world, home an offline player's slot: a cold row.
            ushort cup = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(cup, "client"));
            string cupId = await pair.Host.Run(() => Owned.IdOf(cup));
            string absent = Guid.NewGuid().ToString("D");
            pair.Cleanup(() => pair.Host.Run(() => InGame.Forget(cupId)));
            await Task.Delay(2000);
            await Repaired(pair, cupId, "slot", absent, 4, $"slot 4 of {absent}", "the cup on the ground, home an offline player's slot 4");
            Expect.Equal($"{absent} 4", await pair.Host.Run(() => InGame.ColdRow(cupId)), "the cup's cold row");
            Expect.That(!(await pair.Host.Run(() => Ownership.ById(cupId))).Exists, "the cup has no object on the server");

            if (pair.Dedicated)
            {
                Report.Note("a dedicated host has no player of its own: the host's slot part needs one");
                return;
            }

            // On a player host, the host's own player's slot.
            string host = await pair.Host.Run(() => InGame.HostIdentity());
            ushort lent = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "host"));
            await pair.Host.Run(() => Ownership.Give(lent, "client"));
            string lentId = await pair.Host.Run(() => Owned.IdOf(lent));
            await Task.Delay(2000);
            slot = await pair.Host.Run(() => InGame.FreeSlot(host, lentId));
            await Repaired(pair, lentId, "slot", host, slot, $"slot {slot} of {host}", "the client's mug on the ground, home the host's slot");
            await InSlot(pair, pair.Host, lent, slot, "the mug put in the host's slot");

            // In the host's inventory, home the client's slot: out of the host's hands, into the client's.
            slot = await pair.Host.Run(() => InGame.FreeSlot(client, lentId));
            await Repaired(pair, lentId, "slot", client, slot, $"slot {slot} of {client}", "the mug in the host's inventory, home the client's slot");
            await InSlot(pair, pair.Client, lent, slot, "the mug moved from the host's slot to the client's");
            Expect.Equal(-1, await pair.Host.Run(() => Ledger.LocalSlot(lent)), "the host's game no longer has it in its inventory");
        }

        // The ledger is set to the home, the repair runs, and the ledger ends where expected with the
        // one-home check holding.
        private static async Task Repaired(Pair pair, string id, string kind, string holder, int slot, string expected, string what)
        {
            string before = (await pair.Host.Run(() => Ownership.ById(id))).ToString();
            string home = await pair.Host.Run<string>(s => InGame.Repair(s, id, kind, holder, slot), 30);
            Report.Note($"{what}: was {before}; the ledger now {home}");
            Expect.Equal(expected, home, $"{what}: the ledger home after the repair");
            string[] broken = (await pair.Host.Run(() => Invariant.Check())).Where(l => l.Contains(id)).ToArray();
            Expect.Empty(broken, $"{what}: the one-home check after the repair");
        }

        private static async Task InSlot(Pair pair, Peer who, ushort netId, int slot, string what)
        {
            ServerSlot server = null;
            int local = -1;
            for (int i = 0; i < 20; i++)
            {
                server = await pair.Host.Run(() => Ledger.Where(netId));
                local = await who.Run(() => Ledger.LocalSlot(netId));
                if (server.Slot == slot && local == slot) break;
                await Task.Delay(500);
            }
            Report.Note($"{what}: server {server}, {who.Name} slot {local}");
            Expect.Equal(slot, server.Slot, $"{what}: the server's slot");
            Expect.Equal(slot, local, $"{what}: the {who.Name}'s slot");
        }

        // ---- MODE=strays. A Debug build only reports these breaches, and before R8 a Release build
        // only logged them too: without its repair each case's break stays as it was made, and its
        // checks fail (each case says how).
        private static async Task Strays(Pair pair)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            await OffTheirPoints(pair, client);
            await NoCopy(pair, client);
            await TwoCopies(pair, client);
            await EndedStray(pair, client);
            await CopyOfALostItem(pair, client);
            await ALostCopy(pair, client);
            await SlotClash(pair);
            await ListsFollow(pair, client);
        }

        // 1. LiveAgrees on a coupler and on a hanger (OI-LIST-14). Without the repair, or with PutHome
        // as it was before R8 (an attached or snapped item stayed and the ledger followed it), the
        // lantern stays on its coupler, Attached, and the hung item on its hanger.
        private static async Task OffTheirPoints(Pair pair, string client)
        {
            string flatbed = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "FlatbedEmpty", "client"));
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { flatbed, loco })));

            ushort lantern = await pair.Host.Run<ushort>(s => SaveHomes.InGame.Grant(s, "EOTLantern"));
            string lanternId = await pair.Host.Run(() => Owned.IdOf(lantern));
            Report.Note("the client's lantern: " + await pair.Client.Run<string>(s => SaveHomes.InGame.AttachToCoupler(s, lantern, flatbed), 90));
            await Until.Value(() => pair.Host.Run(() => Items.Record(lantern)), r => r.Placement == "Attached", 20, "the host has the client's lantern attached");
            int slot = await pair.Host.Run(() => InGame.FreeSlot(client, lanternId));
            await HomedAndRepaired(pair, "slot", client, slot, "LiveAgrees", "the lantern on a coupler, home the client's slot", lanternId);
            OwnerInfo off = await pair.Host.Run<OwnerInfo>(s => InGame.WaitFor(s, lanternId, "PlayerInventory", 15), 30);
            LedgerEntry home = await pair.Host.Run(() => Owned.Entry(lanternId));
            Report.Note($"the lantern after the repair: {off}; ledger {home}");
            Expect.That(off.Exists && off.Placement != "Attached", "the lantern's record isn't attached any more", off.ToString());
            Expect.That(home.Place == "LostAndFound" || home.Place == "Slot" && home.Holder == client && home.Slot == slot,
                "the lantern's ledger home: the client's slot, else its Lost and Found", home.Where);
            int onCouplers = await pair.Host.Run<int>(s => AttachedOffline.InGame.OnCouplers(s, flatbed), 30);
            Expect.That(onCouplers <= 0, "no lantern on the flatbed's couplers on the host (-1: not loaded there)", onCouplers.ToString());
            await Track(pair, lanternId);
            await Holds(pair, "the lantern taken off its coupler", lanternId);

            AttachedOffline.Hung hung = await pair.Host.Run<AttachedOffline.Hung>(s => AttachedOffline.InGame.Hang(s, loco), 120);
            Report.Note($"on the hanger {hung.Hanger} in {loco}: a {hung.Prefab} ({hung.Item}), {hung.State}");
            // Settled: it hangs as Attached first and snaps a moment later, and that commit moves the ledger
            // back to its record (item.ledger-stale-home), undoing a break planted before it.
            await Until.Value(() => pair.Host.Run(() => Items.Record(hung.Item)), r => r.Placement == "SnappedAttachment", 20, "the host has the item snapped to the hanger");
            await pair.Host.Run(() => Ownership.Give(hung.Item, "client"));
            string hungId = await pair.Host.Run(() => Owned.IdOf(hung.Item));
            await HomedAndRepaired(pair, "lost", "", -1, "LiveAgrees", "the item on a hanger, home Lost and Found", hungId);
            await Until.Value(() => pair.Host.Run(() => Items.Record(hung.Item)), r => r.Lost, 20, "the hung item collected into the client's Lost and Found");
            Expect.Equal("LostAndFound", (await pair.Host.Run(() => Owned.Entry(hungId))).Place, "the hung item's ledger home");
            Expect.That(!await pair.Host.Run(() => AttachedOffline.InGame.HangerHolds(hung.HangerNetId)), "the hanger is free on the host");
            await Holds(pair, "the item taken off its hanger", hungId);
        }

        // 2. OneHome, no copy: a live entry whose item is nowhere. Without the repair the entry stays
        // with nothing behind it, and Lost and Found has no entry of its id.
        private static async Task NoCopy(Pair pair, string client)
        {
            string id = await pair.Host.Run(() => InGame.RegisterNoCopy(client, "Mug"));
            pair.Cleanup(() => pair.Host.Run(() => InGame.EndIfNoCopy(id)));
            await RepairSeen(pair, "OneHome", "lost", "", -1, "an entry with no copy", id);
            Expect.Equal(client, await pair.Host.Run(() => InGame.LostOwner(id)), "a Lost and Found entry of that id, the client's (\"\": none)");
            Expect.Equal("LostAndFound", (await pair.Host.Run(() => Owned.Entry(id))).Place, "the entry's ledger home");
            await Track(pair, id);
            await Holds(pair, "an entry with no copy", id);
        }

        // 3. OneHome, two copies: two mugs on the ground carry one id. Without the repair both keep
        // it, and nothing is born from it.
        private static async Task TwoCopies(Pair pair, string client)
        {
            ushort a = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            ushort b = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await Task.Delay(2000);   // settled on the ground
            Copy copy = await pair.Host.Run(() => InGame.MakeCopy(a, b, "client"));
            Report.Note($"mug {copy.First} ({copy.Id}) is the client's; mug {copy.Second} carries its id too");
            await RepairSeen(pair, "OneHome", "entry", "", -1, "two mugs with one id", copy.Id);
            Expect.Equal(copy.Id, await pair.Host.Run(() => Owned.IdOf(copy.First)), "the first mug keeps the id");
            Expect.Equal("World", (await pair.Host.Run(() => Items.Record(copy.First))).Placement, "the first mug stays where it is");
            Expect.Equal("World", (await pair.Host.Run(() => Owned.Entry(copy.Id))).Place, "the id's ledger home");
            LedgerEntry[] born = await Until.Value(() => pair.Host.Run(() => InGame.BornFrom(copy.Id)),
                e => e.Length > 0 && e.All(x => x.Place == "LostAndFound"), 20, "the second mug re-minted, in the client's Lost and Found");
            Expect.Equal(1, born.Length, "entries born from the id");
            Expect.That(born[0].Id != copy.Id, "the second mug has a new id", born[0].Id);
            Expect.Equal("Duplicate", born[0].Born, "the second mug's birth");
            Expect.Equal(client, born[0].Owner, "the second mug's owner");
            Expect.Equal(born[0].Id, await pair.Host.Run(() => Owned.IdOf(copy.Second)), "the second mug's record has the new id");
            await Track(pair, born[0].Id);
            await Holds(pair, "two mugs with one id", copy.Id, born[0].Id);
        }

        // 4. NoStray, an ended entry's item: the mug stays after its entry ends. Without the repair it
        // keeps the ended id, and nothing is born from it.
        private static async Task EndedStray(Pair pair, string client)
        {
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(mug, "client"));
            string id = await pair.Host.Run(() => Owned.IdOf(mug));
            await Task.Delay(2000);
            await pair.Host.Run(() => InGame.EndEntry(id));
            await RepairSeen(pair, "NoStray", "", "", -1, "a mug whose entry ended", id);
            LedgerEntry[] born = await Until.Value(() => pair.Host.Run(() => InGame.BornFrom(id)),
                e => e.Length > 0 && e.All(x => x.Place == "LostAndFound"), 20, "the mug re-minted, in the client's Lost and Found");
            LedgerEntry old = await pair.Host.Run(() => Owned.Entry(id));
            Expect.That(old.Known && !old.Live && old.Ended == "DestroyedByGame", "the old id stays ended", old.ToString());
            Expect.Equal(1, born.Length, "entries born from the old id");
            Expect.That(born[0].Id != id, "the mug has a new id", born[0].Id);
            Expect.Equal("Duplicate", born[0].Born, "the mug's new birth");
            Expect.Equal(client, born[0].Owner, "the mug's new entry's owner");
            await Track(pair, born[0].Id);
            await Holds(pair, "a mug whose entry ended", id, born[0].Id);
        }

        // 4b. OneHome, two copies, home Lost and Found: the client's mug is in Lost and Found and a
        // mug in the world carries its id (a collection dupe). Without the repair's copy count, the
        // check's repair home (Lost and Found) sent it to the no-copy repair, which found the stray
        // and put it in Lost and Found under the same id: nothing was born, and it came back at
        // every save.
        private static async Task CopyOfALostItem(Pair pair, string client)
        {
            ushort a = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            ushort b = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await Task.Delay(2000);
            Copy copy = await pair.Host.Run(() => InGame.CopyOfLost(a, b, "client"));
            Report.Note($"mug {copy.First} ({copy.Id}) is in the client's Lost and Found; mug {copy.Second} in the world carries its id");
            await RepairSeen(pair, "OneHome", "entry", "", -1, "a mug in Lost and Found and a copy", copy.Id);
            Expect.Equal(client, await pair.Host.Run(() => InGame.LostOwner(copy.Id)), "the item keeps its Lost and Found entry");
            Expect.Equal("LostAndFound", (await pair.Host.Run(() => Owned.Entry(copy.Id))).Place, "the id's ledger home");
            LedgerEntry[] born = await Until.Value(() => pair.Host.Run(() => InGame.BornFrom(copy.Id)),
                e => e.Length > 0 && e.All(x => x.Place == "LostAndFound"), 20, "the copy re-minted, in the client's Lost and Found");
            Expect.Equal(1, born.Length, "entries born from the id");
            Expect.Equal("Duplicate", born[0].Born, "the copy's birth");
            Expect.Equal(born[0].Id, await pair.Host.Run(() => Owned.IdOf(copy.Second)), "the copy's record has the new id");
            Expect.Equal(client, await pair.Host.Run(() => InGame.LostOwner(born[0].Id)), "the copy's Lost and Found entry, under the new id");
            await Track(pair, copy.Id);
            await Track(pair, born[0].Id);
            await Holds(pair, "a mug in Lost and Found and a copy", copy.Id, born[0].Id);
        }

        // 4c. OneHome, two copies, the copy in Lost and Found: the client's mug lies in the world, its
        // home, and a mug in Lost and Found carries its id. Without the re-key the copy's Lost and
        // Found entry kept the old id: no entry was saved under the new one, and the old id's
        // lookups found the copy. Without the re-key's resend the client's list (not asked for
        // here) still names the copy as it was, and its first click would be refused.
        private static async Task ALostCopy(Pair pair, string client)
        {
            ushort a = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            ushort b = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await Task.Delay(2000);
            Copy copy = await pair.Host.Run(() => InGame.LostCopyOf(a, b, "client"));
            Report.Note($"mug {copy.First} ({copy.Id}) lies in the world; mug {copy.Second} in the client's Lost and Found carries its id");
            await RepairSeen(pair, "OneHome", "entry", "", -1, "a mug and a copy in Lost and Found", copy.Id);
            Expect.Equal(copy.Id, await pair.Host.Run(() => Owned.IdOf(copy.First)), "the mug in the world keeps the id");
            Expect.Equal("World", (await pair.Host.Run(() => Items.Record(copy.First))).Placement, "the mug stays where it is");
            LedgerEntry[] born = await Until.Value(() => pair.Host.Run(() => InGame.BornFrom(copy.Id)),
                e => e.Length > 0 && e.All(x => x.Place == "LostAndFound"), 20, "the copy re-minted, still in Lost and Found");
            Expect.Equal(1, born.Length, "entries born from the id");
            Expect.Equal(client, await pair.Host.Run(() => InGame.LostOwner(born[0].Id)), "the copy's Lost and Found entry, under the new id");
            Expect.Equal("", await pair.Host.Run(() => InGame.LostOwner(copy.Id)), "no Lost and Found entry under the old id");
            string fresh = await pair.Host.Run(() => InGame.HandleOf(born[0].Id));
            string listed = await Until.Value(() => pair.Client.Run(() => InGame.ListedHandles()),
                h => h.Split(',').Contains(fresh), 20, $"the client's Lost and Found list names the copy by its new handle {fresh}");
            string old = await pair.Host.Run(() => InGame.HandleOf(copy.Id));
            Expect.That(!listed.Split(',').Contains(old), "the client's list no longer names the copy by its old handle", listed);
            await Track(pair, copy.Id);
            await Track(pair, born[0].Id);
            await Holds(pair, "a mug and a copy in Lost and Found", copy.Id, born[0].Id);
        }

        // 5. OneItemPerSlot: the host's own player holds its mug in a slot, and an unowned mug's record
        // says it is in that slot too. Without the repair the unowned mug's record stays in the slot.
        // A dedicated host has no player whose inventory the server keeps as objects (a remote
        // client's are rows), so there is no holder to clash in.
        private static async Task SlotClash(Pair pair)
        {
            if (pair.Dedicated)
            {
                Report.Note("a dedicated host has no player of its own: the slot clash needs a holder whose inventory the server keeps as objects");
                return;
            }
            string host = await pair.Host.Run(() => InGame.HostIdentity());
            ushort owned = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "host"));
            await pair.Host.Run(() => Ownership.Give(owned, "host"));
            string ownedId = await pair.Host.Run(() => Owned.IdOf(owned));
            int slot = await pair.Host.Run<int>(s => Items.Stash(s, owned, false), 60);
            Expect.Equal("Slot", (await pair.Host.Run(() => Owned.Entry(ownedId))).Place, "the host's mug's ledger home before the clash");
            ushort loose = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "host"));
            await Task.Delay(2000);
            string left = await pair.Host.Run<string>(s => InGame.ClashAndRepair(s, loose, host, slot), 60);
            Report.Note($"two mugs in slot {slot} of the host's player: breaches left {(left.Length == 0 ? "none" : left)}");
            await Until.Value(() => pair.Host.Run(() => Items.Record(loose)), r => r.Placement == "World", 20, "the unowned mug dropped into the world");
            ServerSlot kept = await pair.Host.Run(() => Ledger.Where(owned));
            Expect.Equal(slot, kept.Slot, "the owned mug's slot on the server");
            Expect.Equal(slot, kept.LedgerSlot, "the owned mug's ledger slot");
            Expect.Equal(slot, await pair.Host.Run(() => Ledger.LocalSlot(owned)), "the owned mug's slot in the host's inventory");
            Expect.Equal(-1, await pair.Host.Run(() => Ledger.LocalSlot(loose)), "the unowned mug isn't in the host's inventory");
            await Holds(pair, "two mugs in one slot", ownedId, $"slot {slot} of {host}");
        }

        // 6. VanillaAgrees: the client's mug lies in the world and is in vanilla's Lost and Found list
        // too. Without the repair it stays in that list.
        private static async Task ListsFollow(Pair pair, string client)
        {
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(mug, "client"));
            string id = await pair.Host.Run(() => Owned.IdOf(mug));
            await Task.Delay(2000);
            VanillaListsSeen before = await pair.Host.Run(() => InGame.IntoLostList(mug));
            Require.That(before.Lost, "the mug is in vanilla's Lost and Found list", before.Text);
            await RepairSeen(pair, "VanillaAgrees", "", "", -1, $"a mug in the world, in vanilla's {before.Text}", id);
            VanillaListsSeen after = await pair.Host.Run(() => InGame.ListsOf(mug));
            Expect.That(after.World && !after.Lost, "the mug is in vanilla's world list, not its Lost and Found list", after.Text);
            Expect.Equal("World", (await pair.Host.Run(() => Owned.Entry(id))).Place, "the mug's ledger home");
            await Holds(pair, "a mug in vanilla's Lost and Found list", id);
        }

        // The check must see the break as made (the case proves nothing otherwise); the repair runs
        // with the violation it builds.
        private static async Task RepairSeen(Pair pair, string rule, string repair, string holder, int slot, string what, params string[] ids)
        {
            string left = await pair.Host.Run<string>(s => InGame.RepairBreach(s, rule, ids, repair, holder, slot), 60);
            Report.Note($"{what}: repaired; breaches left {(left.Length == 0 ? "none" : left)}");
        }

        // The break (the ledger's home set to where the item isn't) and its repair, the entry's home, in
        // one host step: between two, the item's next commit can put the ledger back to its record
        // first (item.ledger-stale-home), and the check sees no break.
        private static async Task HomedAndRepaired(Pair pair, string kind, string holder, int slot, string rule, string what, string id)
        {
            string left = await pair.Host.Run<string>(s => InGame.SetHomeAndRepair(s, kind, holder, slot, rule, new[] { id }), 60);
            Report.Note($"{what}: repaired; breaches left {(left.Length == 0 ? "none" : left)}");
        }

        private static async Task Holds(Pair pair, string what, params string[] ids)
        {
            string[] broken = (await pair.Host.Run(() => Invariant.Check())).Where(l => ids.Any(id => l.Contains(id))).ToArray();
            Expect.Empty(broken, $"{what}: the one-home check after the repair");
        }

        // The item with this id now (a net ID can change on the way through Lost and Found) is
        // removed after the run.
        private static async Task Track(Pair pair, string id)
        {
            ushort now = await pair.Host.Run(() => Owned.NetIdOf(id));
            if (now != 0) await pair.Host.Run(() => Ledger.Track(now));
        }

        public sealed class Copy
        {
            public ushort First, Second;
            public string Id = "";
        }

        public sealed class VanillaListsSeen
        {
            public bool World, Lost, Inventory;
            public string Text = "";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the host's own player's identity.</summary>
            [Step]
            public static string HostIdentity() => NetworkServer.Instance.LocalPlayer.Guid.ToString("D");

            /// <summary>Host: a slot the ledger would give the item in the holder's inventory, off the belt.</summary>
            [Step]
            public static int FreeSlot(string holder, string id)
            {
                Guid item = Guid.Parse(id), who = Guid.Parse(holder);
                for (int s = Multiplayer.Core.Items.Inventory.InventorySlots.FirstBeltSlot - 1; s >= 0; s--)
                    if (ServerLedger.Ledger.CanTake(Home.InSlot(who, s), item, ServerLedger.Ledger.OwnerOf(item)).Length == 0 &&
                        ColdInventories.At(who, s) == null)
                        return s;
                throw new Exception("no free slot off the belt");
            }

            /// <summary>
            /// Host: breaks the one-home rule on purpose (the ledger says <paramref name="kind"/>: "slot"
            /// of the holder, "lost" or "world"), then repairs it as a Release build does. Result: the
            /// ledger's home once the repair has run.
            /// </summary>
            [Step]
            public static IEnumerator Repair(Step<string> step, string id, string kind, string holder, int slot)
            {
                Guid item = Guid.Parse(id);
                Home want = kind switch
                {
                    "slot" => Home.InSlot(Guid.Parse(holder), slot),
                    "lost" => Home.LostAndFound,
                    "world" => Home.World,
                    _ => throw new ArgumentException(kind)
                };
                LiveItems.LiveItem record = LiveItems.GetAll().First(r => r.PersistentItemId == item);
                ServerLedger.Ledger.Repair(item, want, new LedgerCause(ItemMoveReason.HostCorrection, Guid.Empty, "ledger-repair"));
                ServerLedger.PutHome(record);
                float t0 = UnityEngine.Time.realtimeSinceStartup;
                while (UnityEngine.Time.realtimeSinceStartup - t0 < 10f &&
                       (ServerLedger.CheckNow().Any(l => l.Contains(id)) || !Settled(item, want)))
                    yield return null;
                for (int i = 0; i < 30; i++) yield return null;
                step.Result = ServerLedger.Ledger.TryGetHome(item, out Home home) ? home.ToString() : "";
            }

            private static bool Settled(Guid item, Home want) =>
                ServerLedger.Ledger.TryGetHome(item, out Home home) && (home == want || home.Kind == HomeKind.LostAndFound);

            /// <summary>Host: the item's cold row, "holder slot", or "".</summary>
            [Step]
            public static string ColdRow(string id) =>
                ColdInventories.TryGet(Guid.Parse(id), out var row) ? $"{row.PlacementPlayerIdentity:D} {row.Slot}" : "";

            /// <summary>Host: an offline player's item made up for the run goes: its cold row and its entry.</summary>
            [Step]
            public static void Forget(string id)
            {
                Guid item = Guid.Parse(id);
                ColdInventories.Remove(item);
                ServerLedger.Ledger.End(item, ItemRemovalReason.DestroyedByGame, detail: "ledger-repair");
            }

            // ---- MODE=strays

            private const string StraysDetail = "ledger-repair strays";

            /// <summary>
            /// Host: the break the case made, as the Core check sees it (<paramref name="rule"/>, the
            /// first of <paramref name="ids"/>, the repair home it gives: "" none, "lost", "entry" the
            /// entry's home, "slot" of the holder), repaired as a Release build's check repairs it.
            /// Result: the breaches about the ids still left once it has run ("" for none).
            /// </summary>
            [Step]
            public static IEnumerator RepairBreach(Step<string> step, string rule, string[] ids, string repair, string holder, int slot)
            {
                Guid id = Guid.Parse(ids[0]);
                Home? home;
                switch (repair)
                {
                    case "": home = null; break;
                    case "lost": home = Home.LostAndFound; break;
                    case "slot": home = Home.InSlot(Guid.Parse(holder), slot); break;
                    case "entry":
                        if (!ServerLedger.Ledger.TryGet(id, out var entry)) throw new Exception("the ledger has no entry " + ids[0]);
                        home = entry.Home;
                        break;
                    default: throw new ArgumentException("repair: '', lost, entry or slot, not " + repair);
                }
                return RepairNow(step, (InvariantRule)Enum.Parse(typeof(InvariantRule), rule), id, home, ids);
            }

            // The violation the Core check gives (for a slot clash, the slot's; else the id's) with the
            // repair home expected, handed to ServerLedger.Repair as CheckInvariant hands it; then up to
            // 15 s for its breaches to go.
            private static IEnumerator RepairNow(Step<string> step, InvariantRule rule, Guid id, Home? repair, string[] mentions)
            {
                InvariantViolation[] all = Breaches().ToArray();
                InvariantViolation seen = all.FirstOrDefault(v => Same(v, rule, id, repair));
                if (seen.Rule != rule)
                    throw new Exception($"the check doesn't see the break (rule {rule}, {id:D}, repair {Show(repair)}); it sees: " +
                                        (all.Length == 0 ? "nothing" : string.Join("; ", all.Select(Describe))));
                step.Note("the check sees: " + Describe(seen));
                // Each item is repaired once a check (ServerLedger.repaired); this call is a check of its own.
                ServerLedger.repaired.Clear();
                ServerLedger.Repair(seen);
                float t0 = UnityEngine.Time.realtimeSinceStartup;
                while (UnityEngine.Time.realtimeSinceStartup - t0 < 15f && Breaches().Any(v => Same(v, rule, id, repair) || Mentions(v, mentions)))
                    yield return null;
                for (int i = 0; i < 60; i++) yield return null;
                step.Result = string.Join("; ", Breaches().Where(v => Same(v, rule, id, repair) || Mentions(v, mentions)).Select(Describe));
            }

            // What the server's check finds now, without its report or (in Release) its repair.
            private static IReadOnlyList<InvariantViolation> Breaches() =>
                HomeInvariant.Check(ServerLedger.Ledger.All(), ServerLedger.Observe(), ServerLedger.CurrentContext());

            private static bool Same(InvariantViolation v, InvariantRule rule, Guid id, Home? repair) =>
                v.Rule == rule && v.Repair == repair && (rule == InvariantRule.OneItemPerSlot || v.Id == id);

            private static bool Mentions(InvariantViolation v, string[] what) =>
                what.Any(w => v.Id.ToString("D") == w || v.Detail.Contains(w));

            private static string Describe(InvariantViolation v) => $"{v} (repair {Show(v.Repair)})";

            private static string Show(Home? home) => home?.ToString() ?? "none";

            /// <summary>Host: sets the ledger's home for the item ("slot" of the holder, or "lost"), whatever its object is.</summary>
            [Step]
            public static void SetHome(string id, string kind, string holder, int slot)
            {
                Home want = kind switch
                {
                    "slot" => Home.InSlot(Guid.Parse(holder), slot),
                    "lost" => Home.LostAndFound,
                    _ => throw new ArgumentException(kind)
                };
                LedgerCommit commit = ServerLedger.Ledger.Repair(Guid.Parse(id), want, new LedgerCause(ItemMoveReason.HostCorrection, Guid.Empty, StraysDetail));
                if (!commit.Committed) throw new Exception($"the ledger refused {want}: {commit.Refusal}");
            }

            /// <summary>Host: <see cref="SetHome"/> and <see cref="RepairBreach"/> (the entry's home) in the same
            /// frame, so nothing can commit the item in between.</summary>
            [Step]
            public static IEnumerator SetHomeAndRepair(Step<string> step, string kind, string holder, int slot, string rule, string[] ids)
            {
                SetHome(ids[0], kind, holder, slot);
                IEnumerator repair = RepairBreach(step, rule, ids, "entry", "", -1);
                while (repair.MoveNext())
                    yield return repair.Current;
            }

            /// <summary>Host: waits up to <paramref name="seconds"/> for the item's record to be at
            /// <paramref name="placement"/>. Result: whose it is and where then.</summary>
            [Step]
            public static IEnumerator WaitFor(Step<OwnerInfo> step, string id, string placement, float seconds)
            {
                float t0 = UnityEngine.Time.realtimeSinceStartup;
                while (UnityEngine.Time.realtimeSinceStartup - t0 < seconds && Ownership.ById(id).Placement != placement)
                    yield return null;
                step.Result = Ownership.ById(id);
            }

            /// <summary>Host: a live entry of the owner's for a <paramref name="prefab"/> lying in the
            /// world, with no object or row anywhere. Result: its id.</summary>
            [Step]
            public static string RegisterNoCopy(string owner, string prefab)
            {
                Registration made = ServerLedger.Ledger.Register(new BirthRequest
                {
                    Id = Guid.NewGuid(),
                    Owner = OwnerRef.Player(Guid.Parse(owner)),
                    Prefab = prefab,
                    Home = Home.World,
                    Reason = BirthReason.Granted,
                    Detail = StraysDetail + ": no copy"
                });
                if (!made.Accepted) throw new Exception("the ledger refused the entry: " + made.Refusal);
                return made.Entry.Id.ToString("D");
            }

            /// <summary>Host: an entry still with no copy (the repair didn't make one) ends, so the run leaves none behind.</summary>
            [Step]
            public static void EndIfNoCopy(string id)
            {
                Guid item = Guid.Parse(id);
                if (ServerLedger.Ledger.IsLive(item) && !LiveItems.GetAll().Any(r => r.PersistentItemId == item) &&
                    !LostAndFoundServer.TryGetEntry(item, out _))
                    ServerLedger.Ledger.End(item, ItemRemovalReason.DestroyedByGame, detail: StraysDetail);
            }

            /// <summary>Host: the handle the item's id goes by in players' lists.</summary>
            [Step]
            public static string HandleOf(string id) => Multiplayer.Server.Items.Projection.ItemHandles.Of(Guid.Parse(id)).ToString();

            /// <summary>Client: the handles in this player's Lost and Found list as the server last sent it (not asked for).</summary>
            [Step]
            public static string ListedHandles() =>
                string.Join(",", Multiplayer.Client.Items.LostAndFound.LostAndFoundList.Items.Select(row => row.Handle.ToString()));

            /// <summary>Host: the owner Lost and Found's entry of this id names, or "" for no entry.</summary>
            [Step]
            public static string LostOwner(string id) =>
                !LostAndFoundServer.TryGetEntry(Guid.Parse(id), out var entry) ? ""
                : Guid.TryParse(entry.OwnerIdentity, out Guid owner) ? owner.ToString("D") : entry.OwnerIdentity;

            /// <summary>
            /// Host: of two mugs lying in the world, the first the server's records list is made the
            /// owner's, and the second given its id (its identity and its record), as a dupe bug would
            /// leave them. The repair keeps the copy in the ledger's home it walks to first
            /// (KeepOneCopy): both lie in the world, its home, so the first record.
            /// </summary>
            [Step]
            public static Copy MakeCopy(ushort a, ushort b, string owner)
            {
                List<ushort> order = LiveItems.GetAll().Select(r => r.NetId).ToList();
                if (order.IndexOf(a) < 0 || order.IndexOf(b) < 0) throw new Exception("the mugs have no records");
                ushort first = order.IndexOf(a) < order.IndexOf(b) ? a : b, second = first == a ? b : a;
                Ownership.Give(first, owner);
                LiveItems.TryGet(first, out LiveItems.LiveItem original);
                LiveItems.TryGet(second, out LiveItems.LiveItem copy);
                if (!NetworkedItem.TryGet(second, out NetworkedItem item) || item == null) throw new Exception("no object for " + second);
                var identity = item.GetComponent<Multiplayer.Server.Items.SaveLoad.ItemIdentity>() ??
                               item.gameObject.AddComponent<Multiplayer.Server.Items.SaveLoad.ItemIdentity>();
                identity.Value = original.PersistentItemId;
                copy.PersistentItemId = original.PersistentItemId;
                return new Copy { First = first, Second = second, Id = original.PersistentItemId.ToString("D") };
            }

            /// <summary>
            /// Host: mug <paramref name="a"/> is made the owner's and sent to their Lost and Found;
            /// mug <paramref name="b"/>, lying in the world, is given its id (its identity and record).
            /// </summary>
            [Step]
            public static Copy CopyOfLost(ushort a, ushort b, string owner)
            {
                Guid id = Lose(a, owner);
                GiveId(b, id);
                return new Copy { First = a, Second = b, Id = id.ToString("D") };
            }

            /// <summary>
            /// Host: mug <paramref name="a"/> is made the owner's where it lies; mug <paramref name="b"/>
            /// goes to their Lost and Found, takes <paramref name="a"/>'s id (its identity, record and
            /// Lost and Found entry), and its own entry ends, as a dupe bug would leave it.
            /// </summary>
            [Step]
            public static Copy LostCopyOf(ushort a, ushort b, string owner)
            {
                Ownership.Give(a, owner);
                if (!LiveItems.TryGet(a, out LiveItems.LiveItem original)) throw new Exception("no record for " + a);
                Guid own = Lose(b, owner);
                GiveId(b, original.PersistentItemId);
                if (!LostAndFoundServer.RekeyItem(b, original.PersistentItemId)) throw new Exception("Lost and Found has no entry for " + b);
                if (!ServerLedger.Ledger.End(own, ItemRemovalReason.DestroyedByGame, detail: StraysDetail + ": the copy's own entry"))
                    throw new Exception("the ledger didn't end " + own);
                return new Copy { First = a, Second = b, Id = original.PersistentItemId.ToString("D") };
            }

            private static Guid Lose(ushort netId, string owner)
            {
                Ownership.Give(netId, owner);
                if (!LiveItems.TryGet(netId, out LiveItems.LiveItem record)) throw new Exception("no record for " + netId);
                if (!ServerLedger.SendToLostAndFound(record.PersistentItemId, LostItemReason.SaveRecovery, StraysDetail, out string refused))
                    throw new Exception($"{netId} wasn't collected: {refused}");
                return record.PersistentItemId;
            }

            // An item in Lost and Found is data (no object): only its record carries the id.
            private static void GiveId(ushort netId, Guid id)
            {
                if (!LiveItems.TryGet(netId, out LiveItems.LiveItem record)) throw new Exception("no record for " + netId);
                if (NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null)
                {
                    var identity = item.GetComponent<Multiplayer.Server.Items.SaveLoad.ItemIdentity>() ??
                                   item.gameObject.AddComponent<Multiplayer.Server.Items.SaveLoad.ItemIdentity>();
                    identity.Value = id;
                }
                record.PersistentItemId = id;
            }

            /// <summary>Host: the ledger ends the item's entry, its object left where it is.</summary>
            [Step]
            public static void EndEntry(string id)
            {
                if (!ServerLedger.Ledger.End(Guid.Parse(id), ItemRemovalReason.DestroyedByGame, detail: StraysDetail + ": ended, the object kept"))
                    throw new Exception("the ledger didn't end " + id);
            }

            /// <summary>Host: the live entries born a Duplicate from this id.</summary>
            [Step]
            public static LedgerEntry[] BornFrom(string id)
            {
                Guid from = Guid.Parse(id);
                return ServerLedger.Ledger.All()
                    .Where(e => e.IsLive && e.Born.Reason == BirthReason.Duplicate && e.Born.From == from)
                    .Select(e => Owned.Entry(e.Id.ToString("D"))).ToArray();
            }

            /// <summary>
            /// Host: the unowned mug's record is made to say it is in <paramref name="slot"/> of the
            /// holder (as <c>ServerLedger.HomeOf</c> reads it), where the holder's owned mug is, and
            /// the clash is repaired at once. Result: the breaches of that slot still left.
            /// </summary>
            [Step]
            public static IEnumerator ClashAndRepair(Step<string> step, ushort loose, string holder, int slot)
            {
                Guid who = Guid.Parse(holder);
                if (!LiveItems.TryGet(loose, out LiveItems.LiveItem record)) throw new Exception("no record for " + loose);
                record.Placement = ItemPlacementKind.PlayerInventory;
                record.Holder = who;
                record.Slot = slot;
                record.PlacementHand = PlayerHand.None;
                Home home = Home.InSlot(who, slot);
                return RepairNow(step, InvariantRule.OneItemPerSlot, Guid.Empty, home, new[] { home.ToString() });
            }

            /// <summary>Host: the item is put in vanilla's Lost and Found list too, as no path of the
            /// mod's may (OI-VAN-06). Result: the lists it is in then.</summary>
            [Step]
            public static VanillaListsSeen IntoLostList(ushort netId)
            {
                StorageController.Instance.StorageLostAndFound.AddItem(Items.Find(netId).Item);
                return ListsOf(netId);
            }

            /// <summary>Host: vanilla's lists the item is in.</summary>
            [Step]
            public static VanillaListsSeen ListsOf(ushort netId)
            {
                StorageMembership lists = StorageIntegration.GetMembership(Items.Find(netId).Item);
                return new VanillaListsSeen
                {
                    World = lists.HasFlag(StorageMembership.World),
                    Lost = lists.HasFlag(StorageMembership.LostAndFound),
                    Inventory = lists.HasFlag(StorageMembership.Inventory),
                    Text = lists.ToString()
                };
            }
        }
    }
}
