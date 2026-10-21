using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Known item bug (expected to fail on beta, docs/research/item-bug-review.md): the client leaves
    /// its own item in a loco's cab and quits, and the car is deleted (a comms-radio delete, the
    /// unused-car despawn, a job or garage respawn). The deleted car's items go to their owners' Lost
    /// and Found, but a disconnect zeroed the owner's number, so Lost and Found refuses the item
    /// ("item-has-no-persistent-owner") and the server destroys it as "not a player's item"
    /// (NetworkedLostAndFoundManager.CollectFromDeletedCar). CONNECTED=1 is the control: the client
    /// stays, and the item must reach its Lost and Found.
    /// </summary>
    [Scenario("item-bug-car-delete-offline-owner", "Known bug: an offline player's item in a deleted car must go to their Lost and Found, not be destroyed")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class ItemBugCarDeleteOfflineOwner : Scenario<ItemBugCarDeleteOfflineOwner.Options>
    {
        public sealed class Options
        {
            /// <summary>The item's prefab.</summary>
            public string Prefab = "Lantern";
            /// <summary>The control run: the client stays connected while the car is deleted.</summary>
            public bool Connected;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "host"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));
            ushort item = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"));
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, item, "drop", 90), 90);
            await Task.Delay(3000);
            ItemRecord before = await pair.Host.Run(() => Items.Record(item));
            Report.Note($"the client's {o.Prefab} {item} in {loco}'s cab: {before.Placement} parent {before.ParentNetId}, owner {before.Owner}");
            Require.That(before.Placement == "TrainInterior", "the item is in the loco's interior", before.Placement);
            Require.That(before.Owner == before.ClientId, "the item is the client's", $"owner {before.Owner}, client {before.ClientId}");

            if (!o.Connected)
            {
                await pair.Client.Quit();
                await Task.Delay(6000);   // the host notices the departure
                ItemRecord gone = await pair.Host.Run(() => Items.Record(item));
                Report.Note($"after the client left: owner number {gone.Owner}");
            }
            string[] left = await pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco }));
            Require.That(left.Length == 0, "the loco is deleted");
            await Task.Delay(2000);
            ItemRecord after = await pair.Host.Run(() => Items.Record(item));
            Report.Note($"after the delete: exists {after.Exists}, in Lost and Found {after.Lost}, {after.Placement}, last move {after.LastMove} {after.LastMoveDetail}");
            if (!o.Connected) await pair.Rejoin();
            Expect.That(after.Exists, $"the {o.Prefab} still exists after its car was deleted", "destroyed");
            Expect.That(after.Lost, $"the {o.Prefab} is in its owner's Lost and Found", after.Placement);
        }
    }
}
