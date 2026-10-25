using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// An offline owner counts as far away (lost-and-found.md, "The collection"): their items lying anywhere but with
    /// a player go to their Lost and Found under the usual rules, in a train's cab as well as on the
    /// ground. The client leaves two mugs, one on the ground and one in a loco's cab, and the host
    /// gives both to a player who isn't here. A third mug, the client's own, also goes in the cab.
    /// With the client nearby nothing is collected. Once it walks away, both absent player's mugs
    /// are collected, and the online client's own mug stays in the cab: an online owner's items are
    /// collected from the world only.
    /// </summary>
    [Scenario("offline-lost-and-found", "An absent owner's mugs on the ground and in a cab go to their Lost and Found once nobody is near; an online owner's cab mug stays")]
    public sealed class OfflineLostAndFound : Scenario<OfflineLostAndFound.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 50, Nearby = 100, Grace = 1 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));

            // The absent player's mugs are theirs from the start; the client carries them where they go.
            ushort ground = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            AbsentOwner.Absent a = await pair.Host.Run(() => AbsentOwner.InGame.GiveToAbsent(ground));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(ground), 60);
            ushort cab = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            AbsentOwner.Absent b = await pair.Host.Run(() => AbsentOwner.InGame.GiveToAbsent(cab));
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, cab, "drop", 90), 90);
            ushort mine = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(mine), 60);
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, mine, "drop", 90), 90);

            Expect.Equal(a.Owner, (await pair.Host.Run(() => AbsentOwner.InGame.Read(ground))).Owner, "the ground mug's owner after the client carried it (the absent player)");
            ItemRecord cabRecord = await pair.Host.Run(() => Items.Record(cab));
            Report.Note($"absent player's mugs: {ground} on the ground, {cab} in the cab ({cabRecord.Placement}); the client's own {mine} in the cab");
            Require.Equal("TrainInterior", cabRecord.Placement, "the cab mug's placement");

            await Task.Delay(5000);   // longer than the grace period, with the client nearby
            Expect.That(!(await pair.Host.Run(() => Items.Record(ground))).Lost, "with the client nearby the ground mug isn't collected");
            Expect.That(!(await pair.Host.Run(() => Items.Record(cab))).Lost, "with the client nearby the cab mug isn't collected");

            await pair.Client.Run<Abs>(s => Walk.Away(s, 400, 0));
            if (!pair.Dedicated) await pair.Host.Run<Abs>(s => Walk.Away(s, 400, 20));
            await Until.Value(() => pair.Host.Run(() => Items.Record(ground)), r => r.Lost, 30, "the absent player's ground mug collected");
            await Until.Value(() => pair.Host.Run(() => Items.Record(cab)), r => r.Lost, 30, "the absent player's cab mug collected");
            Expect.Equal(a.Id, await pair.Host.Run(() => Owned.LostEntryId(ground)), "the ground mug's entry (its own id)");
            Expect.Equal(b.Id, await pair.Host.Run(() => Owned.LostEntryId(cab)), "the cab mug's entry (its own id)");
            ItemRecord own = await pair.Host.Run(() => Items.Record(mine));
            Expect.That(!own.Lost && own.Placement == "TrainInterior", "the online client's own cab mug stays in the cab", own.ToString());
        }
    }
}
