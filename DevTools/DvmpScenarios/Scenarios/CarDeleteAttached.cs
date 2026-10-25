using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Cars are deleted with items on them, not in them (R6 C2; OI-LF-09, OI-LF-10): the client's
    /// end-of-train lantern on one flatbed's coupler, an unowned lantern on another's, and the
    /// client's gadget installed in a loco. The owned lantern and the gadget go to the client's
    /// Lost and Found; the unowned lantern comes off the coupler into the world beside the track, and
    /// both games still have it. Nothing is destroyed. The distance sweep is held off for the run, so
    /// only the delete can collect them. An unowned mug lies in the loco's cab, and comes out into the
    /// world as the unowned lantern does.
    /// HOW=car deletes the cars one by one (CarSpawner.DeleteCar). HOW=slow deletes them as one list
    /// over a few frames (DeleteTrainCars: vanilla prepares and hides a car every two frames and
    /// deletes them after), the loco last: an item taken off every car up front fell back into a car
    /// still there (J1), and a gadget came off after its car's NetId had gone (J2).
    /// </summary>
    [Scenario("car-delete-attached", "Deleted cars with a lantern on a coupler and a gadget installed: owned ones go to Lost and Found, an unowned lantern stays in the world")]
    [Budget(600)]
    public sealed class CarDeleteAttached : Scenario<CarDeleteAttached.Options>
    {
        public sealed class Options
        {
            /// <summary>How the cars are deleted: car (one by one) or slow (one list, over a few frames).</summary>
            public string How = "car";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 50, Nearby = 100, Grace = 900 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string owned = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "FlatbedEmpty", "client"));
            string unowned = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "FlatbedEmpty", "client"));
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "client"));
            string[] cars = { owned, unowned, loco };
            bool deleted = false;
            pair.Cleanup(() => deleted ? Task.CompletedTask : pair.Host.Run<string[]>(s => Cars.Delete(s, cars)));

            ushort lantern = await pair.Host.Run<ushort>(s => SaveHomes.InGame.Grant(s, "EOTLantern"));
            string lanternId = await pair.Host.Run(() => Owned.IdOf(lantern));
            Report.Note("the client's lantern: " + await pair.Client.Run<string>(s => SaveHomes.InGame.AttachToCoupler(s, lantern, owned), 90));
            ushort stray = await pair.Host.Run<ushort>(s => Items.Spawn(s, "EOTLantern", "client"));
            await pair.Client.Run<int>(s => Items.Stash(s, stray, false), 60);
            Report.Note("the unowned lantern: " + await pair.Client.Run<string>(s => SaveHomes.InGame.AttachToCoupler(s, stray, unowned), 90));
            string gadget = await pair.Host.Run<string>(s => SaveHomes.InGame.InstallFor(s, loco));
            ushort mug = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "client"));
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, mug, "drop", 90), 90);
            await pair.Client.Run<Abs>(s => Walk.Away(s, 0, 12));
            await Until.Value(() => pair.Host.Run(() => Items.Record(lantern)), r => r.Placement == "Attached", 20, "the host has the client's lantern attached");
            await Until.Value(() => pair.Host.Run(() => Items.Record(stray)), r => r.Placement == "Attached", 20, "the host has the unowned lantern attached");
            ItemRecord strayBefore = await pair.Host.Run(() => Items.Record(stray));
            Require.That(strayBefore.Owner == 0, "nobody owns the second lantern", "owner " + strayBefore.Owner);
            Require.That((await pair.Host.Run(() => Owned.Entry(gadget))).Place == "Installed", "the gadget is installed on the loco");
            await Until.Value(() => pair.Host.Run(() => Items.Record(mug)), r => r.Placement == "TrainInterior", 20, "the host has the unowned mug in the loco");

            string[] left = o.How == "slow"
                ? await pair.Host.Run<string[]>(s => InGame.DeleteSlowly(s, new[] { loco, owned, unowned }))
                : await pair.Host.Run<string[]>(s => Cars.Delete(s, cars));
            deleted = true;
            Require.That(left.Length == 0, "the cars are deleted", string.Join(", ", left));
            await Task.Delay(3000);

            LedgerEntry lanternAfter = await pair.Host.Run(() => Owned.Entry(lanternId));
            LedgerEntry gadgetAfter = await pair.Host.Run(() => Owned.Entry(gadget));
            ItemRecord strayAfter = await pair.Host.Run(() => Items.Record(stray));
            Report.Note($"after the delete: lantern {lanternAfter}; gadget {gadgetAfter}; unowned lantern {strayAfter.Placement}, exists {strayAfter.Exists}");
            Expect.Equal("LostAndFound", lanternAfter.Place, "the client's lantern's ledger home after its car was deleted");
            Expect.Equal("LostAndFound", gadgetAfter.Place, "the client's gadget's ledger home after its loco was deleted");
            Expect.That(strayAfter.Exists, "the unowned lantern still exists after its car was deleted", "destroyed");
            Expect.Equal("World", strayAfter.Placement, "the unowned lantern's placement after the delete");
            ItemPose host = await pair.Host.Run(() => Items.Pose(stray));
            ItemPose client = await pair.Client.Run(() => Items.Pose(stray));
            Report.Note($"the unowned lantern: host {host}, client {client}");
            Expect.That(client.Present, "the client still has the unowned lantern", "missing");
            if (host.Present && client.Present)
                Expect.That(Peers.Distance(host.Pos, client.Pos) < 2f, "the host and client have the unowned lantern in the same place",
                    $"{Peers.Distance(host.Pos, client.Pos):0.##} m apart");
            Expect.That(!host.State.Contains("Attached"), "the unowned lantern is off the coupler on the host", host.State);

            ItemRecord mugAfter = await pair.Host.Run(() => Items.Record(mug));
            Report.Note($"the unowned mug after the delete: exists {mugAfter.Exists}, {mugAfter.Placement}, last move {mugAfter.LastMove} {mugAfter.LastMoveDetail}");
            Expect.That(mugAfter.Exists, "the unowned mug still exists after its loco was deleted", "destroyed");
            Expect.Equal("World", mugAfter.Placement, "the unowned mug's placement after the delete");
            Expect.That((await pair.Client.Run(() => Items.Pose(mug))).Present, "the client still has the unowned mug", "missing");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: deletes the cars as one list over a few frames (DeleteTrainCars), the
            /// first one last. Result: the IDs still there 5 s later.</summary>
            [Step]
            public static IEnumerator DeleteSlowly(Step<string[]> step, string[] ids)
            {
                var cars = ids.Select(Trains.Find).ToList();
                CarSpawner.Instance.DeleteTrainCars(cars);
                yield return new WaitForSecondsRealtime(5f);
                step.Result = CarSpawner.Instance.AllCars.Where(c => c != null && ids.Contains(c.ID)).Select(c => c.ID).ToArray();
            }
        }
    }
}
