using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A loco is deleted with a player's mug in its cab (item-review L1; lost-and-found.md, "A deleted car"). The
    /// mug must go to its owner's Lost and Found, not be destroyed. Before, an offline owner's number
    /// was 0, so Lost and Found refused the mug ("item-has-no-persistent-owner") and the car delete
    /// destroyed it. The mug's owner is the client, still connected (OWNER=online, the control), or
    /// gone (OWNER=offline: it quits before the delete and rejoins after it), or a player who isn't
    /// here (OWNER=absent). The distance sweep is held off for the run, so only the delete can collect it.
    /// After the delete the mug isn't turned back on in the world on the host by the loco's interior
    /// teardown, which used to still list it (L3).
    /// </summary>
    [Scenario("car-delete-owner", "A loco deleted with a mug in its cab sends the mug to its owner's Lost and Found, whether the owner is online, gone or never here (L1)")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class CarDeleteOwner : Scenario<CarDeleteOwner.Options>
    {
        public sealed class Options
        {
            /// <summary>The mug's owner: online (the client), offline (the client, gone at the delete) or absent (a made-up player).</summary>
            public string Owner = "offline";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 50, Nearby = 100, Grace = 900 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "client"));
            bool deleted = false;
            pair.Cleanup(() => deleted ? Task.CompletedTask : pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));

            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            if (o.Owner == "absent") await pair.Host.Run(() => AbsentOwner.InGame.GiveToAbsent(mug));
            else await pair.Client.Run(() => LostAndFound.InGame.Claim(mug), 60);
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, mug, "drop", 90), 90);
            await pair.Client.Run<Abs>(s => Walk.Away(s, 0, 8));   // off the train
            OwnerInfo before = await pair.Host.Run(() => Ownership.Read(mug));
            Report.Note($"mug in the cab: {before}");
            Require.Equal("TrainInterior", before.Placement, "the mug's placement before the delete");
            if (o.Owner == "absent") Require.That(before.Absent, "the mug's owner is an identity with no session", before.Owner);
            else Require.That(before.OwnerConnected, "the mug's owner (the client) has a session", before.Owner);

            bool left = o.Owner == "offline";
            if (left)
            {
                await pair.Client.Quit();
                // A killed client counts until its connection times out, which takes longer than a quit.
                await Until.Value(() => pair.Host.Run(() => Ownership.RemotePlayers()), n => n == 0, 60, "the host to notice the client left");
                await Task.Delay(2000);   // its departure is handled
            }
            try
            {
                OwnerInfo gone = await pair.Host.Run(() => Ownership.Read(mug));
                Report.Note($"before the delete: {gone}");
                await pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco }));
                deleted = true;
                OwnerInfo after = await Until.Value(() => pair.Host.Run(() => Ownership.Read(mug)), v => v.Lost || !v.Exists, 20, "the mug collected or gone after the delete");
                Report.Note($"after the delete: {after}");
                Expect.That(after.Exists, "the mug still exists after the loco was deleted");
                Expect.That(after.Lost, "the mug is in Lost and Found after the loco was deleted");
                Expect.Equal(before.Id, after.LostEntry, "the mug's Lost and Found entry (its own id)");
                Expect.Equal(before.Owner, after.Owner, "the mug's owner after the delete");
                // Once the loco's interior has gone, the collected mug is still put away on the host: the car
                // no longer lists it, so its teardown doesn't turn it back on in the world (L3).
                await Task.Delay(3000);
                Expect.That(!await pair.Host.Run(() => InGame.ActiveInWorld(mug)), "the collected mug isn't back in the world on the host after the delete");
            }
            finally
            {
                if (left) await pair.Rejoin();
            }
            if (!left) return;

            await Task.Delay(10000);   // the rejoined client's Lost and Found binds
            OwnerInfo back = await pair.Host.Run(() => Ownership.Read(mug));
            Report.Note($"after the rejoin: {back}");
            Expect.That(back.OwnerConnected, "the mug's owner (the client) has a session again after the rejoin", back.Owner);
            uint mugHandle = await pair.Host.Run(() => Owned.HandleOf(mug));   // the list names rows by handle (R2)
            int slot = await pair.Client.Run<int>(s => LostAndFound.InGame.Retrieve(s, mugHandle), 60);
            Report.Note($"the client retrieved it into slot {slot}");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Whether the item has an object here that is on, in the world.</summary>
            [Step]
            public static bool ActiveInWorld(ushort netId) =>
                Multiplayer.Shared.Items.LiveState.NetworkedItem.TryGet(netId, out var item) && item != null && item.gameObject.activeInHierarchy;
        }
    }
}
