using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Shared.Items.LiveState;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A real owner goes offline (owned-items.md, PRs 2a and 3). The client claims a mug and leaves
    /// it on the ground (or in a loco's cab, WHERE=cab), and stashes a second one. Then it quits (or
    /// its game is killed, LEAVE=crash). Its left mug must keep its owner identity and get the absent
    /// number, not 0. On a player host, once nobody is near, it goes to the client's Lost and Found under
    /// its own id, and not to vanilla's list behind the server's back (item-review L4); after the
    /// rejoin the entry has the client's new number and the client retrieves it. On a dedicated host
    /// nobody is connected then, so nothing is collected: it waits where it is, theirs. The stashed mug is back in the
    /// client's inventory, still theirs.
    /// </summary>
    [Scenario("owner-offline", "A client that quits or crashes keeps its left mug (absent number, its identity) and gets it in Lost and Found; after the rejoin it retrieves it, and its stashed mug is back")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class OwnerOffline : Scenario<OwnerOffline.Options>
    {
        public sealed class Options
        {
            /// <summary>How the client leaves: quit or crash (its game killed).</summary>
            public string Leave = "quit";
            /// <summary>Where the left mug is: ground or cab (a loco's cab).</summary>
            public string Where = "ground";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 50, Nearby = 100, Grace = 1 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            if (!pair.Dedicated)
                Require.That(client != await pair.Host.Run(() => Owned.HostIdentity()), "the client and the host's player have different identities");

            ushort left = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(left), 60);
            if (o.Where == "cab")
            {
                string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "client"));
                pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));
                await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, left, "drop", 90), 90);
                await pair.Client.Run<Abs>(s => Walk.Away(s, 0, 8));   // off the train, onto the ground beside it
            }
            ushort kept = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run<int>(s => Items.Stash(s, kept, false), 60);
            await Task.Delay(2000);   // the stash reaches the host's record
            OwnerInfo before = await pair.Host.Run(() => Ownership.Read(left));
            string keptId = await pair.Host.Run(() => Owned.IdOf(kept));
            Report.Note($"left mug {before}; stashed mug {kept} ({keptId})");
            Require.Equal(client, before.Owner, "the left mug's owner (the client)");
            Require.Equal(before.ClientNumber, before.Number, "the left mug's owner number (the client's)");
            if (o.Where == "cab") Require.Equal("TrainInterior", before.Placement, "the left mug's placement");

            if (o.Leave == "crash") await pair.Client.Kill();
            else await pair.Client.Quit();
            try
            {
                // A killed client counts until its connection times out, which takes longer than a quit.
                await Until.Value(() => pair.Host.Run(() => Ownership.RemotePlayers()), n => n == 0, 60, "the host to notice the client left");
                await Task.Delay(2000);   // its departure is handled
                OwnerInfo away = await pair.Host.Run(() => Ownership.Read(left));
                Report.Note($"after the client left: {away}");
                Expect.Equal(client, away.Owner, "the left mug's owner once the client left (still the client)");
                Expect.Equal(AbsentOwner.AbsentNumber, away.Number, "the left mug's owner number once the client left (absent, not 0)");

                if (pair.Dedicated)
                {
                    // With nobody connected nothing is collected: no player's position is known
                    // (owned-items.md, PR 3). The mug waits where it is, still the client's.
                    await Task.Delay(8000);
                    OwnerInfo waiting = await pair.Host.Run(() => Ownership.Read(left));
                    Expect.That(!waiting.Lost, "with nobody connected the mug isn't collected", waiting.ToString());
                    Expect.That(!await pair.Host.Run(() => InGame.VanillaOnly(left)), "vanilla's Lost and Found doesn't take the mug behind the server's back (L4)");
                }
                else
                {
                    await pair.Host.Run<Abs>(s => Walk.Away(s, 400, 20));
                    OwnerInfo lost = await Until.Value(() => pair.Host.Run(() => Ownership.Read(left)), v => v.Lost, 30, "the offline client's mug collected into its Lost and Found");
                    Expect.Equal(lost.Id, lost.LostEntry, "the entry's id (the mug's own)");
                    Expect.Equal(client, lost.Owner, "the owner of the collected mug");
                    Expect.Equal("LostAndFound", lost.LedgerWhere, "where the ledger has the collected mug");
                    Expect.That(!await pair.Host.Run(() => InGame.VanillaOnly(left)), "vanilla's Lost and Found doesn't hold the mug without the server's entry (L4)");
                }
            }
            finally
            {
                await pair.Rejoin();
            }

            await Task.Delay(10000);   // the rejoined client receives its items and its Lost and Found binds
            OwnerInfo back = await pair.Host.Run(() => Ownership.Read(left));
            Report.Note($"after the rejoin: {back}");
            Expect.Equal(back.ClientNumber, back.Number, "the left mug's owner number after the rejoin (the client's new number)");
            if (!pair.Dedicated)
            {
                Expect.That(await pair.Host.Run(() => Ownership.LostCount(client)) >= 1, "the client's Lost and Found lists the mug after the rejoin");
                int slot = await pair.Client.Run<int>(s => LostAndFound.InGame.Retrieve(s, left), 60);
                await Task.Delay(2000);
                ItemRecord got = await pair.Host.Run(() => Items.Record(left));
                Report.Note($"retrieved into slot {slot}; host {got.Placement} for P{got.PlacementPlayer}");
                Expect.Equal("PlayerInventory", got.Placement, "the retrieved mug's placement");
                Expect.Equal(back.ClientNumber, got.PlacementPlayer, "who has the retrieved mug (the client)");
            }

            ushort keptNow = await pair.Host.Run(() => Owned.NetIdOf(keptId));
            Expect.That(keptNow != 0, "the stashed mug still exists after the rejoin", keptId);
            if (keptNow != 0)
            {
                OwnerInfo k = await pair.Host.Run(() => Ownership.Read(keptNow));
                Expect.Equal(client, k.Owner, "the stashed mug's owner after the rejoin");
                Expect.Equal(k.ClientNumber, k.Number, "the stashed mug's owner number after the rejoin");
                Expect.That(await Until.Value(() => pair.Client.Run(() => Owned.InInventory(keptNow)), v => v, 20, "the stashed mug back in the client's inventory"),
                    "the stashed mug is in the client's inventory after the rejoin");
            }
            Expect.Empty(await pair.Host.Run<string[]>(s => Owned.AuditFor(s, new[] { back.Id, keptId })), "ledger audit problems for the two mugs");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: whether vanilla's Lost and Found list holds the item while the server has no entry for it.</summary>
            [Step]
            public static bool VanillaOnly(ushort netId)
            {
                if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item?.Item == null) return false;
                bool vanilla = StorageController.Instance?.StorageLostAndFound?.ContainsItem(item.Item) == true;
                return vanilla && !NetworkedLostAndFoundManager.Contains(netId);
            }
        }
    }
}
