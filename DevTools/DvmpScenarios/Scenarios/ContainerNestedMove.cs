using System.Threading.Tasks;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A move between a box and the box inside it isn't refused as stale (docs/design/items/
    /// containers.md, "Moving between boxes"; OI-CON-13). The container UI sends the open box's
    /// revision as both the source's and the destination's, which for an item in the inner box,
    /// moved out into the outer one it has open, used to be refused. The client's box holds its inner
    /// box, which holds its mug. The client moves the mug up into the outer box, as it would with the
    /// outer box open, then back down into the inner box with that one open: both accepted, the mug's
    /// home following. A move with an old revision of the open box is still refused.
    /// </summary>
    [Scenario("container-nested-move", "Moving an item between a box and the box inside it, with the open box's revision as the UI sends it, is accepted; an old revision is still refused (OI-CON-13)")]
    [Budget(600)]
    public sealed class ContainerNestedMove : Scenario<ContainerNestedMove.Options>
    {
        public sealed class Options
        {
            /// <summary>The inner box's prefab, tried first; the outer is one whose box takes it.</summary>
            public string Container = "ItemContainerToolbox";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            string[] nest = (await pair.Host.Run<string>(s => Boxes.Nesting(s, o.Container, "Mug"), 120)).Split(' ');
            if (nest.Length != 2) throw new HarnessException("no container the game has takes another that takes a mug");
            ushort outer = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, nest[0], "client"));
            ushort inner = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, nest[1], "client"));
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Host.Run(() => Ownership.Give(mug, "client"));
            string outerId = await pair.Host.Run(() => Owned.IdOf(outer));
            string innerId = await pair.Host.Run(() => Owned.IdOf(inner));
            string mugId = await pair.Host.Run(() => Owned.IdOf(mug));
            pair.Cleanup(async () =>
            {
                ushort[] left = await pair.Host.Run(() => Items.Existing(new[] { outer, inner, mug }));
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, left), 120);
            });

            Require.Equal(Boxes.Accepted, await pair.Client.Run<string>(s => Boxes.Deposit(s, inner, mug), 120), "the mug into the inner box");
            Require.Equal(Boxes.Accepted, await pair.Client.Run<string>(s => Boxes.Deposit(s, outer, inner), 120), "the inner box into the outer one");
            uint mugHandle = await pair.Host.Run(() => Boxes.HandleOf(mugId));
            uint outerHandle = await pair.Host.Run(() => Boxes.HandleOf(outerId));
            uint innerHandle = await pair.Host.Run(() => Boxes.HandleOf(innerId));

            // Up: from the inner box into the outer one, the outer box open.
            uint outerRevision = await pair.Host.Run(() => Boxes.RevisionOf(outerId));
            string up = await pair.Client.Run<string>(s => Boxes.MoveAsOpen(s, mugHandle, outerHandle, outerRevision, 1), 30);
            Report.Note("up into the outer box: " + up);
            Expect.Equal(Boxes.Accepted, up, "the move up from the inner box, with the outer box's revision");
            Expect.Equal(outerId, await pair.Host.Run(() => Boxes.StoredIn(mugId)), "the box the mug is in after the move up");

            // A stale revision of the open box is still refused.
            uint innerRevision = await pair.Host.Run(() => Boxes.RevisionOf(innerId));
            string stale = await pair.Client.Run<string>(s => Boxes.MoveAsOpen(s, mugHandle, innerHandle, innerRevision - 1, 0), 30);
            Report.Note("down with an old revision: " + stale);
            Expect.Equal("container-revision-stale", stale, "a move with an old revision of the open box");

            // Down: from the outer box back into the inner one, the inner box open.
            string down = await pair.Client.Run<string>(s => Boxes.MoveAsOpen(s, mugHandle, innerHandle, innerRevision, 0), 30);
            Report.Note("down into the inner box: " + down);
            Expect.Equal(Boxes.Accepted, down, "the move down from the outer box, with the inner box's revision");
            Expect.Equal(innerId, await pair.Host.Run(() => Boxes.StoredIn(mugId)), "the box the mug is in after the move down");
            Expect.Equal("Container", (await pair.Host.Run(() => Owned.Entry(mugId))).Place, "the mug's ledger home");
            Expect.Equal("ok", await pair.Host.Run(() => Boxes.GraphCheck()), "the graph after the moves");
        }
    }
}
