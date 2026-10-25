using System.Threading.Tasks;
using Dvmp.Lib;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A player leaves with an item in their hand (OI-JOIN-06, D5; docs/design/items/ledger.md, "Cold
    /// and live homes"). The client holds a mug and quits (or its game is killed, LEAVE=crash):
    /// <list type="bullet">
    /// <item>its own (HOLD=own, the default) or one nobody owns (HOLD=unowned) goes into a free slot
    /// of its inventory, a cold row with no object anywhere, and is back in that slot after the rejoin;</item>
    /// <item>someone else's (HOLD=borrowed: the host's player's on a player host, an absent player's on a
    /// dedicated one) goes to its owner's Lost and Found at once, still theirs (<c>BorrowerLeft</c>).</item>
    /// </list>
    /// Never left where the client stood, under a lease or unsaved.
    /// </summary>
    [Scenario("leave-holding", "A client leaving with its own, an unowned or a borrowed mug in its hand: a free slot (back after the rejoin), or the owner's Lost and Found (OI-JOIN-06)")]
    [Needs(Need.RestartsGames)]
    [Budget(300)]
    public sealed class LeaveHolding : Scenario<LeaveHolding.Options>
    {
        public sealed class Options
        {
            /// <summary>Whose mug the client holds: own, unowned or borrowed.</summary>
            public string Hold = "own";
            /// <summary>How the client leaves: quit or crash (its game killed).</summary>
            public string Leave = "quit";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            ushort mug;
            string owner;
            bool ownerConnected = true;
            switch (o.Hold)
            {
                case "own":
                    mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
                    await pair.Host.Run(() => Ownership.Give(mug, "client"));
                    owner = client;
                    break;
                case "unowned":
                    mug = await pair.Host.Run<ushort>(s => Items.Spawn(s, "Mug", "client"));
                    owner = "";
                    break;
                case "borrowed" when pair.Dedicated:
                    mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
                    AbsentOwner.Absent a = await pair.Host.Run(() => AbsentOwner.InGame.GiveToAbsent(mug));
                    (owner, ownerConnected) = (a.Owner, a.OwnerConnected);
                    Require.That(!ownerConnected, "the absent owner has no session", owner);
                    break;
                case "borrowed":
                    mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
                    await pair.Host.Run(() => Ownership.Give(mug, "host"));
                    owner = await pair.Host.Run(() => Owned.HostIdentity());
                    break;
                default:
                    throw new HarnessException("HOLD is own, unowned or borrowed, not " + o.Hold);
            }
            string id = await pair.Host.Run(() => Owned.IdOf(mug));
            Require.That(id.Length > 0, "the mug has an id", mug.ToString());
            pair.Cleanup(async () =>
            {
                ushort netId = await pair.Host.Run(() => Owned.NetIdOf(id));
                if (netId != 0) await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { netId }), 60);
            });
            await pair.Client.Run<int>(s => Items.Stash(s, mug, true), 60);
            await Task.Delay(2000);   // the take reaches the host's record
            ItemRecord before = await pair.Host.Run(() => Items.Record(mug));
            Report.Note($"the {o.Hold} mug {mug} ({id}), owner {(owner.Length > 0 ? owner : "nobody")}: {before}");
            Require.Equal("PlayerHand", before.Placement, "where the client has the mug");

            if (o.Leave == "crash") await pair.Client.Kill();
            else await pair.Client.Quit();
            int slot = -2;
            try
            {
                // A killed client counts until its connection times out, which takes longer than a quit.
                await Until.Value(() => pair.Host.Run(() => Ownership.RemotePlayers()), n => n == 0, 60, "the host to notice the client left");
                await Task.Delay(2000);   // its departure is handled
                if (o.Hold == "borrowed")
                {
                    OwnerInfo after = await pair.Host.Run(() => Ownership.Read(mug));
                    Report.Note("after the client left: " + after);
                    Expect.Equal("LostAndFound", after.Placement, "where the borrowed mug went when the client left");
                    Expect.Equal(owner, after.Owner, "the borrowed mug's owner after the client left");
                    Expect.Equal(ownerConnected, after.OwnerConnected, "whether its owner has a session (as before)");
                    Expect.Equal(after.Id, after.LostEntry, "the mug's Lost and Found entry (its own id)");
                    return;
                }
                string state = await Until.Value(() => pair.Host.Run(() => ColdHomes.InGame.State(id)), s => s == "cold", 30,
                    "the mug is a cold row once the client has left");
                slot = await pair.Host.Run(() => ColdHomes.InGame.RowSlot(id));
                Report.Note($"after the client left: {state}, slot {slot}");
                Expect.That(slot >= 0, "the held mug went to a free slot of the client's inventory", $"slot {slot}");
                if (o.Hold == "own")
                {
                    Expect.Equal(slot, await pair.Host.Run(() => ColdHomes.InGame.LedgerSlot(id)), "the ledger's slot for the cold mug");
                    LedgerEntry e = await pair.Host.Run(() => Owned.Entry(id));
                    Expect.Equal(client, e.Owner, "the cold mug's owner (still the client)");
                }
            }
            finally
            {
                await pair.Rejoin();
            }

            await Task.Delay(12000);   // the client receives its items
            ushort now = await pair.Host.Run(() => Owned.NetIdOf(id));
            Require.That(now != 0, "the mug is live again after the rejoin", id);
            Expect.That(await Until.Value(() => pair.Client.Run(() => Owned.InInventory(now)), v => v, 20, "the mug back in the client's inventory"),
                "the mug is in the client's inventory after the rejoin");
            ServerSlot where = await pair.Host.Run(() => Ledger.Where(now));
            Report.Note("after the rejoin: " + where);
            Expect.Equal(slot, where.Slot, "the mug's slot after the rejoin (the one it went cold in)");
        }
    }
}
