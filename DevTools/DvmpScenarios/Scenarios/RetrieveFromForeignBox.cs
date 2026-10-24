using System;
using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Retrieval out of someone else's box inside the owner's own (owned-list.md, "Retrieval"; R5
    /// stack review F4). The client puts its mug in a box nobody owns, and the box is then given to
    /// a player who isn't here (offline B). The client puts B's box in its own toolbox and keeps the
    /// toolbox in hand, then retrieves the mug from its owned-items list. The mug comes out alone into
    /// the client's Lost and Found: B's box stays in the toolbox, still B's, and the toolbox stays in
    /// the client's hand. Before F4 this was refused as nothing to do, because the outermost box was
    /// with its owner, though the client can't open B's box.
    /// </summary>
    [Scenario("retrieve-from-foreign-box", "F4: the owner's mug in offline B's box, inside the owner's toolbox in hand, is retrieved alone; B's box stays")]
    [Budget(600)]
    public sealed class RetrieveFromForeignBox : Scenario<RetrieveFromForeignBox.Options>
    {
        public sealed class Options
        {
            /// <summary>The boxes' prefab: one that takes a box of its own kind is found from it.</summary>
            public string Container = "ItemContainerToolbox";
            public string Prefab = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 5000, Nearby = 5000, Grace = 900 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            string prefab = await pair.Host.Run<string>(s => Boxes.SelfNesting(s, o.Container), 120);
            if (prefab.Length == 0) throw new HarnessException("no container prefab takes a box of its own kind");
            ushort toolbox = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, prefab, "client"));
            ushort foreign = await pair.Host.Run<ushort>(s => Items.Spawn(s, prefab, "client"));
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"));
            string toolboxId = await pair.Host.Run(() => Owned.IdOf(toolbox));
            string foreignId = await pair.Host.Run(() => Owned.IdOf(foreign));
            string mugId = await pair.Host.Run(() => Owned.IdOf(mug));
            string[] ids = { toolboxId, foreignId, mugId };
            pair.Cleanup(async () =>
            {
                ushort[] left = await pair.Host.Run(() => MagazineTools.InGame.NetIdsOf(ids));
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, left), 120);
            });
            Report.Note($"boxes {prefab}: the client's {toolbox} ({toolboxId}), B's {foreign} ({foreignId}); {o.Prefab} {mug} ({mugId})");

            // The mug in a box nobody owns, which then becomes offline B's.
            Require.Equal(Boxes.Accepted, await pair.Client.Run<string>(s => Boxes.Deposit(s, foreign, mug), 120), "the mug into the box nobody owns");
            await pair.Client.Run(() => Items.Drop(), 10);
            OwnerInfo b = await pair.Host.Run(() => Ownership.Give(foreign, "absent"));
            Report.Note($"B's box: {b}");
            Require.That(b.Absent, "the box is an offline player's", b.ToString());
            Require.Equal(foreignId, await pair.Host.Run(() => Boxes.StoredIn(mugId)), "the box the mug is in");

            // B's box in the client's toolbox, held.
            Require.Equal(Boxes.Accepted, await pair.Client.Run<string>(s => Boxes.Deposit(s, toolbox, foreign), 120), "B's box into the client's toolbox");
            Require.Equal(toolboxId, await pair.Host.Run(() => Boxes.StoredIn(foreignId)), "the box B's box is in");
            Require.Equal(toolbox, await pair.Client.Run(() => Items.Held()), "the client's toolbox in its hand");
            await Task.Delay(1000);

            OwnedRetrieve.Result r = await Retrieve(pair, client, mugId);
            Expect.That(r.Accepted, "the client's retrieval of its mug from B's box is accepted", r.Reason);
            await Task.Delay(2000);
            OwnerInfo mugNow = await pair.Host.Run(() => Ownership.ById(mugId));
            Report.Note($"the mug after the retrieval: {mugNow}");
            Expect.That(mugNow.Lost && mugNow.Owner == client, "the mug is in the client's Lost and Found", mugNow.ToString());
            Expect.Equal("", await pair.Host.Run(() => Boxes.StoredIn(mugId)), "the box the mug is in after the retrieval (none)");
            Expect.Equal(toolboxId, await pair.Host.Run(() => Boxes.StoredIn(foreignId)), "B's box stays in the client's toolbox");
            LedgerEntry foreignNow = await pair.Host.Run(() => Owned.Entry(foreignId));
            Expect.Equal(b.Owner, foreignNow.Owner, "B's box is still B's");
            Expect.Equal("Container", foreignNow.Place, "B's box's ledger home (the toolbox)");
            OwnerInfo toolboxNow = await pair.Host.Run(() => Ownership.ById(toolboxId));
            Expect.That(!toolboxNow.Lost, "the client's toolbox didn't go to Lost and Found", toolboxNow.ToString());
            Expect.Equal(toolbox, await pair.Client.Run(() => Items.Held()), "the client's toolbox still in its hand");
            Expect.Empty(await pair.Host.Run<string[]>(s => Owned.AuditFor(s, ids)), "the ledger's audit of this run's items");
            await pair.Client.Run(() => Items.Drop(), 10);
        }

        // The client asks for its owned-items list, and retrieves the item by its row's handle.
        private static async Task<OwnedRetrieve.Result> Retrieve(Pair pair, string client, string itemId)
        {
            uint handle = await pair.Host.Run(() => OwnedRetrieve.InGame.HandleOf(client, itemId));
            if (handle == 0) return new OwnedRetrieve.Result { Reason = "not in the server's list" };
            return await pair.Client.Run<OwnedRetrieve.Result>(s => OwnedRetrieve.InGame.Retrieve(s, handle), 30);
        }
    }
}
