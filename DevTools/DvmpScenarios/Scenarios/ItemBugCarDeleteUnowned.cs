using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Known item bug LF2 (expected to fail on beta, docs/research/item-bug-review.md): an item nobody
    /// owns (a mug, a job booklet, the shop's scanner) lies in a loco's cab when the car is deleted.
    /// The deleted car's items go to their owners' Lost and Found, and the server destroys every
    /// item Lost and Found refuses (NetworkedLostAndFoundManager.CollectFromDeletedCar). Vanilla
    /// puts a deleted car's items back in the world (TrainCar.InteriorOnDestroy), and a scene item
    /// such as the scanner destroyed this way is tombstoned in the save, gone for good.
    /// </summary>
    [Scenario("item-bug-car-delete-unowned", "Known bug LF2: an unowned item in a deleted car's cab must stay in the world, as in vanilla, not be destroyed")]
    [KnownBug("LF2")]
    [Budget(300)]
    public sealed class ItemBugCarDeleteUnowned : Scenario<ItemBugCarDeleteUnowned.Options>
    {
        public sealed class Options
        {
            /// <summary>The item's prefab (spawned unowned, not personal).</summary>
            public string Prefab = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "host"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));
            ushort item = await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "client"));
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, item, "drop", 90), 90);
            await Task.Delay(3000);
            ItemRecord before = await pair.Host.Run(() => Items.Record(item));
            Report.Note($"the {o.Prefab} {item} in {loco}'s cab: {before.Placement} parent {before.ParentNetId}, owner {before.Owner}");
            Require.That(before.Placement == "TrainInterior", "the item is in the loco's interior", before.Placement);
            Require.That(before.Owner == 0, "nobody owns the item", "owner " + before.Owner);

            string[] left = await pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco }));
            Require.That(left.Length == 0, "the loco is deleted");
            await Task.Delay(2000);
            ItemRecord after = await pair.Host.Run(() => Items.Record(item));
            Report.Note($"after the delete: exists {after.Exists}, {after.Placement}, last move {after.LastMove} {after.LastMoveDetail}");
            Expect.That(after.Exists, $"the {o.Prefab} still exists after its car was deleted", "destroyed");
        }
    }
}
