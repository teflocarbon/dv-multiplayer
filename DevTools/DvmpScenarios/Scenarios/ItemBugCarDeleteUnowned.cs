using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Item bugs LF2 and LF5 (fixed in R6 C2, docs/research/item-bug-review.md): an item nobody owns
    /// (a mug, a job booklet, the shop's scanner) lies in a loco's cab when the car is deleted. It
    /// must stay in the world, beside the track, as vanilla puts a deleted car's items back
    /// (TrainCar.InteriorOnDestroy), and every player must still see it. Before, the server destroyed
    /// every item Lost and Found refused, and a scene item destroyed this way was tombstoned in the
    /// save, gone for good. HOW=car deletes the loco on its own (CarSpawner.DeleteCar); HOW=track
    /// deletes everything on its track (DeleteTrainCarsFromTrack, which skipped the item patch: LF5).
    /// </summary>
    [Scenario("item-bug-car-delete-unowned", "LF2/LF5: an unowned item in a deleted car's cab stays in the world beside the track, as in vanilla, not destroyed (HOW=car or track)")]
    [Budget(300)]
    public sealed class ItemBugCarDeleteUnowned : Scenario<ItemBugCarDeleteUnowned.Options>
    {
        public sealed class Options
        {
            /// <summary>The item's prefab (spawned unowned, not personal).</summary>
            public string Prefab = "Mug";
            /// <summary>car: delete the loco alone; track: delete everything on its track.</summary>
            public string How = "car";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "host"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));
            ushort item = await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "client"));
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, item, "drop", 90), 90);
            await pair.Client.Run<Abs>(s => Walk.Away(s, 0, 8));   // off the train: a track delete doesn't step players off
            await Task.Delay(3000);
            ItemRecord before = await pair.Host.Run(() => Items.Record(item));
            Report.Note($"the {o.Prefab} {item} in {loco}'s cab: {before.Placement} parent {before.ParentNetId}, owner {before.Owner}");
            Require.That(before.Placement == "TrainInterior", "the item is in the loco's interior", before.Placement);
            Require.That(before.Owner == 0, "nobody owns the item", "owner " + before.Owner);

            string[] left = o.How == "track"
                ? await pair.Host.Run<string[]>(s => InGame.DeleteTrack(s, loco))
                : await pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco }));
            Require.That(left.Length == 0, "the loco is deleted");
            await Task.Delay(3000);
            ItemRecord after = await pair.Host.Run(() => Items.Record(item));
            Report.Note($"after the delete: exists {after.Exists}, {after.Placement}, last move {after.LastMove} {after.LastMoveDetail}");
            Expect.That(after.Exists, $"the {o.Prefab} still exists after its car was deleted", "destroyed");
            Expect.Equal("World", after.Placement, $"the {o.Prefab}'s placement after the delete");
            Expect.That(!after.Lost, "an unowned item doesn't go to Lost and Found");
            ItemPose host = await pair.Host.Run(() => Items.Pose(item));
            ItemPose client = await pair.Client.Run(() => Items.Pose(item));
            Report.Note($"host {host}, client {client}");
            Expect.That(client.Present, $"the client still has the {o.Prefab}", "missing");
            if (host.Present && client.Present)
                Expect.That(Peers.Distance(host.Pos, client.Pos) < 2f, "the host and client have it in the same place",
                    $"{Peers.Distance(host.Pos, client.Pos):0.##} m apart");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: deletes everything on the car's track as the yard's cleanup does
            /// (DeleteTrainCarsFromTrack, over a few frames). Result: the car, if it's still there.</summary>
            [Step]
            public static IEnumerator DeleteTrack(Step<string[]> step, string car)
            {
                TrainCar c = Trains.Find(car);
                RailTrack track = c.Bogies.Select(b => b.track).FirstOrDefault(t => t != null)
                    ?? throw new System.Exception($"{car} is on no track");
                CarSpawner.Instance.DeleteTrainCarsFromTrack(track);
                yield return new WaitForSecondsRealtime(3f);
                step.Result = CarSpawner.Instance.AllCars.Where(t => t != null && t.ID == car).Select(t => t.ID).ToArray();
            }
        }
    }
}
