using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A shelf item's whole life (R7; docs/design/items/world-items.md, "Shelves"). The client drops a
    /// mug of its own on the floor of the nearest shed, as <c>owned-list-shelf</c> does: the server has
    /// it on that shelf and the client sees it there (SH1). The client then leaves and joins again, a
    /// late joiner, and is shown the mug on the shelf (SH2, OI-SYNC-04). After a save and a restart the
    /// mug is still on the shelf in both games, the shelf found by its key in each (OI-SYNC-09), and
    /// the client retrieves it from its owned-items list into its Lost and Found (OI-LIST-09).
    /// </summary>
    [Scenario("shelf-item", "A client's mug on a shelf (a shed): on the server's shelf, shown to the client after it joins again, still there after a restart, and retrieved from the owned-items list")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class ShelfItem : Scenario<ShelfItem.Options>
    {
        public sealed class Options
        {
            /// <summary>"drop" at the client's feet, or "throw" onto the floor.</summary>
            public string Release = "drop";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            ushort mug = await OwnedListShelf.Shelve(pair, new OwnedListShelf.Options { Who = "client", Release = o.Release, Seen = true });
            string id = await pair.Host.Run(() => Owned.IdOf(mug));

            // A late joiner: the client leaves the mug on the shelf and joins again.
            await KeepUncollected(pair);
            await pair.Client.Quit();
            await pair.Rejoin();
            await Task.Delay(12000);
            await SeenOnShelf(pair, mug, "after the client joins again");

            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart();
            await Task.Delay(12000);   // the client receives its items
            await KeepUncollected(pair);
            mug = await pair.Host.Run(() => Owned.NetIdOf(id));
            Require.That(mug != 0, "the mug is in the world after the restart");
            ItemRecord record = await pair.Host.Run(() => Items.Record(mug));
            Report.Note($"host after the restart: {record.Placement} on {record.ParentKind}");
            Expect.Equal("StaticParent", record.ParentKind, "what the mug is on after the restart");
            Expect.Equal("Shelf", (await pair.Host.Run(() => Owned.Entry(id))).Place, "the mug's ledger home after the restart");
            Expect.Equal("on a shelf", await pair.Host.Run(() => LedgerRows.InGame.RowWhere(client, id)), "the client's owned-items row after the restart");
            await SeenOnShelf(pair, mug, "after the restart");

            OwnedRetrieve.Result r = await Retrieve(pair, client, id);
            Expect.That(r.Accepted, "the shelved mug's retrieval is accepted", r.Reason);
            OwnerInfo now = await pair.Host.Run(() => Ownership.ById(id));
            Expect.That(now.Lost && now.LostEntry == id, "the mug is in the client's Lost and Found", now.ToString());
        }

        // Nothing goes to Lost and Found while its owner is away, or after the restart resets it.
        private static async Task KeepUncollected(Pair pair)
        {
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 5000, Nearby = 5000, Grace = 900 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
        }

        private static async Task SeenOnShelf(Pair pair, ushort mug, string when)
        {
            Abs at = await pair.Host.Run(() => OwnedListShelf.InGame.At(mug));
            await pair.Client.Run(() => OwnedListShelf.InGame.GoTo(at), 60);
            string seen = "";
            for (int i = 0; i < 20; i++)
            {
                seen = await pair.Client.Run(() => OwnedListShelf.InGame.Copy(mug));
                if (seen.Contains("parent OldShed")) break;
                await Task.Delay(1000);
            }
            Report.Note($"client copy {when}: {seen}");
            Expect.That(seen.Contains("parent OldShed"), $"the client has the mug on the shelf {when}", seen);
        }

        private static async Task<OwnedRetrieve.Result> Retrieve(Pair pair, string client, string itemId)
        {
            uint handle = await pair.Host.Run(() => OwnedRetrieve.InGame.HandleOf(client, itemId));
            if (handle == 0) return new OwnedRetrieve.Result { Reason = "not in the server's list" };
            return await pair.Client.Run<OwnedRetrieve.Result>(s => OwnedRetrieve.InGame.Retrieve(s, handle), 30);
        }
    }
}
