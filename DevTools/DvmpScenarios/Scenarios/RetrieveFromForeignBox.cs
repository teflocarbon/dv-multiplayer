using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dvmp.Lib;
using HarmonyLib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Server.Items.Ledger;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Retrieval out of someone else's box inside the owner's own (owned-list.md, "Retrieval"; R5
    /// stack review F4). The client puts its mug in a box of its own and that box in its crate,
    /// which it keeps in hand (a deposit takes only the depositor's own items, so this is the only way
    /// in). The host then makes the inner box a player's who isn't here (offline B), as a test can and
    /// no game path does: the ledger never changes an owner. The client retrieves the mug from its
    /// owned-items list. The mug comes out alone into the client's Lost and Found: B's box stays
    /// in the crate, still B's, and the crate stays in the client's hand. Before F4 this was refused as nothing to do, because the outermost box was
    /// with its owner, though the client can't open B's box.
    /// </summary>
    [Scenario("retrieve-from-foreign-box", "F4: the owner's mug in offline B's box, inside the owner's toolbox in hand, is retrieved alone; B's box stays")]
    [Budget(600)]
    public sealed class RetrieveFromForeignBox : Scenario<RetrieveFromForeignBox.Options>
    {
        public sealed class Options
        {
            /// <summary>B's (inner) box's prefab, tried first; the client's outer box is one that takes it.</summary>
            public string Container = "ItemContainerToolbox";
            public string Prefab = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 5000, Nearby = 5000, Grace = 900 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            string[] nest = (await pair.Host.Run<string>(s => Boxes.Nesting(s, o.Container, o.Prefab), 120)).Split(' ');
            if (nest.Length != 2) throw new HarnessException("no container the game has takes another that takes the item");
            ushort toolbox = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, nest[0], "client"));
            ushort foreign = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, nest[1], "client"));
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
            Report.Note($"boxes {nest[0]} and {nest[1]}: the client's {toolbox} ({toolboxId}), B's {foreign} ({foreignId}); {o.Prefab} {mug} ({mugId})");

            // The mug in the client's own box, that box in its crate, held.
            Require.Equal(Boxes.Accepted, await pair.Client.Run<string>(s => Boxes.Deposit(s, foreign, mug), 120), "the mug into the client's box");
            await pair.Client.Run(() => Items.Drop(), 10);
            Require.Equal(Boxes.Accepted, await pair.Client.Run<string>(s => Boxes.Deposit(s, toolbox, foreign), 120), "the box into the client's toolbox");
            Require.Equal(toolboxId, await pair.Host.Run(() => Boxes.StoredIn(foreignId)), "the box the inner box is in");
            Require.Equal(foreignId, await pair.Host.Run(() => Boxes.StoredIn(mugId)), "the box the mug is in");
            Require.Equal(toolbox, await pair.Client.Run(() => Items.Held()), "the client's toolbox in its hand");

            // The inner box becomes offline B's.
            string b = await pair.Host.Run(() => InGame.ReownToAbsent(foreignId));
            Report.Note($"the inner box is now {b}'s, who isn't here");
            Require.Equal(b, (await pair.Host.Run(() => Owned.Entry(foreignId))).Owner, "the inner box's owner (B)");
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
            Expect.Equal(b, foreignNow.Owner, "B's box is still B's");
            Expect.Equal("Container", foreignNow.Place, "B's box's ledger home (the crate)");
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

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: makes the item a player's who isn't here (a new identity), in the ledger itself:
            /// a test's shortcut for an item stored in a box, which has no live record to give.
            /// Result: the new owner's identity.
            /// </summary>
            [Step]
            public static string ReownToAbsent(string id)
            {
                Guid item = Guid.Parse(id), absent = Guid.NewGuid();
                var entries = Traverse.Create(ServerLedger.Ledger).Field("entries").GetValue<Dictionary<Guid, Multiplayer.Core.Items.Ledger.LedgerEntry>>();
                if (!entries.TryGetValue(item, out var entry)) throw new Exception("no ledger entry for " + id);
                AccessTools.Property(typeof(Multiplayer.Core.Items.Ledger.LedgerEntry), "Owner").SetValue(entry, OwnerRef.Player(absent));
                return absent.ToString("D");
            }
        }
    }
}
