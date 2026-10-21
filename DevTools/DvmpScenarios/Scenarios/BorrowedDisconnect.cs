using System.Collections;
using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Core.Items;
using Multiplayer.Server.Items;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Session;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A player leaves with someone else's item (the item audit: borrowed items left with the
    /// player; owned-items.md, "Leaving"). The client takes a mug into its inventory (or keeps it
    /// in its hand, HOLD=hand) and quits (or its game is killed, LEAVE=crash). The mug must go
    /// straight to its owner's Lost and Found, still theirs, not stay with the departed player.
    /// The owner is the host's own player (OWNER=host, a player host only) or a player who isn't
    /// here (OWNER=absent, PR 3: before, an offline owner's number was 0 and the borrower kept it).
    /// The client rejoins afterwards.
    /// </summary>
    [Scenario("borrowed-disconnect", "A client leaving with the host's or an absent player's mug sends it to its owner's Lost and Found, still theirs")]
    [Needs(Need.RestartsGames)]
    public sealed class BorrowedDisconnect : Scenario<BorrowedDisconnect.Options>
    {
        public sealed class Options
        {
            /// <summary>Whose the mug is: host (the host's own player) or absent (a player who isn't here).</summary>
            public string Owner = "host";
            /// <summary>Where the client has it when it leaves: inventory or hand.</summary>
            public string Hold = "inventory";
            /// <summary>How the client leaves: quit or crash (its game killed).</summary>
            public string Leave = "quit";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            if (o.Owner == "host" && pair.Dedicated) throw new HarnessException("OWNER=host needs a player host: a dedicated host has no player to own the mug");
            ushort item;
            string owner;
            int number;
            if (o.Owner == "absent")
            {
                item = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
                AbsentOwner.Absent a = await pair.Host.Run(() => AbsentOwner.InGame.GiveToAbsent(item));
                (owner, number) = (a.Owner, a.Number);
                Require.Equal(AbsentOwner.AbsentNumber, number, "the absent owner's number");
            }
            else
            {
                item = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "host"));
                Owned own = await pair.Host.Run<Owned>(s => InGame.Own(s, item));
                Require.Equal(own.Host, own.Owner, "the mug's owner (the host's number)");
                (owner, number) = (await pair.Host.Run(() => Dvmp.Scenarios.Owned.HostIdentity()), own.Owner);
            }
            Report.Note($"mug {item} belongs to {o.Owner} {owner} (number {number})");

            await pair.Client.Run<int>(s => Items.Stash(s, item, o.Hold == "hand"), 60);
            await Task.Delay(2000);   // the stash reaches the host's record
            ItemRecord before = await pair.Host.Run(() => Items.Record(item));
            Report.Note($"host record: {before.Placement} for P{before.PlacementPlayer}, owner P{before.Owner}");
            Require.Equal(o.Hold == "hand" ? "PlayerHand" : "PlayerInventory", before.Placement, "where the client has the mug");
            if (o.Leave == "crash") await pair.Client.Kill();
            else await pair.Client.Quit();
            try
            {
                await Task.Delay(6000);   // the host notices the departure
                OwnerInfo after = await pair.Host.Run(() => Ownership.Read(item));
                Report.Note($"after the client left: {after}");
                // Straight to its owner's Lost and Found (owned-items.md, "Leaving"), not left where the client stood.
                Expect.Equal("LostAndFound", after.Placement, "where the mug went when the client left");
                Expect.Equal(owner, after.Owner, "the mug's owner after the client left");
                Expect.Equal(number, after.Number, "the mug's owner number after the client left");
                Expect.Equal(after.Id, after.LostEntry, "the mug's Lost and Found entry (its own id)");
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
