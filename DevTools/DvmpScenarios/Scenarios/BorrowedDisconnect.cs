using System.Collections;
using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Server.Items;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Session;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A player leaves with someone else's item (the item audit: borrowed items left with the
    /// player). The host's mug goes into the client's inventory; the client quits. The mug must be
    /// put down in the world, still the host's, not kept in the departed player's inventory. The
    /// client rejoins afterwards. The host owns the mug as a player: a player host only.
    /// </summary>
    [Scenario("borrowed-disconnect", "A client leaving with the host's item puts it down in the world, still the host's")]
    [Needs(Need.PlayerHost, Need.RestartsGames)]
    public sealed class BorrowedDisconnect : Scenario<BorrowedDisconnect.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort item = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "host"));
            Owned own = await pair.Host.Run<Owned>(s => InGame.Own(s, item));
            Report.Note($"mug {item} belongs to the host (owner P{own.Owner}, host P{own.Host})");
            Require.Equal(own.Host, own.Owner, "the mug's owner (the host's number)");

            await pair.Client.Run<int>(s => Items.Stash(s, item, false), 60);
            await Task.Delay(2000);   // the stash reaches the host's record
            ItemRecord before = await pair.Host.Run(() => Items.Record(item));
            Report.Note($"host record: {before.Placement} for P{before.PlacementPlayer}, owner P{before.Owner}");
            await pair.Client.Quit();
            try
            {
                await Task.Delay(6000);   // the host notices the departure
                ItemRecord after = await pair.Host.Run(() => Items.Record(item));
                Report.Note($"after the client left: {after.Placement}, owner P{after.Owner}, in Lost and Found {after.Lost}");
                // Straight to its owner's Lost and Found (owned-items.md, "Leaving"), not left where the client stood.
                Expect.Equal("LostAndFound", after.Placement, "where the host's mug went when the client left");
                Expect.Equal(own.Host, after.Owner, "the mug's owner after the client left (the host's number)");
            }
            finally
            {
                await pair.Rejoin();
            }
        }

        public sealed class Owned
        {
            public int Owner, Host;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: makes the item a personal one (as bought items are), picks it up so it
            /// becomes the host's, and puts it down again.</summary>
            [Step]
            public static IEnumerator Own(Step<Owned> step, ushort id)
            {
                NetworkedItem.TryGet(id, out NetworkedItem n);
                n.Item.InventorySpecs.BelongsToPlayer = true;   // as a bought item is
                yield return Hands.Pickup(id);
                for (int i = 0; i < 30; i++) yield return null;
                yield return Hands.Drop();
                for (int i = 0; i < 60; i++) yield return null;
                AuthoritativeItemRegistry.TryGet(id, out var r);
                step.Result = new Owned { Owner = r?.PersistentOwnerPlayerId ?? 0, Host = NetworkLifecycle.Instance.LocalSession.PlayerId };
            }
        }
    }
}
