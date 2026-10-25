using System.Threading.Tasks;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A station fixture can't go into a player's inventory (access.md, "Fixtures"; the hook
    /// shops PR 2c fills, PR 1c). The host marks the mug as a fixture for the run (in memory) and the
    /// client tries to stash one: the server must refuse it, and the client mustn't keep it in its
    /// inventory. Then, with the mark taken off, the same client stashes another mug as before.
    /// </summary>
    [Scenario("fixture-no-stash", "A station fixture can't be stashed: the server refuses it and the client doesn't keep it; an ordinary item still stashes")]
    public sealed class FixtureNoStash : Scenario<FixtureNoStash.Options>
    {
        public sealed class Options
        {
            /// <summary>The prefab to mark as a fixture for the run.</summary>
            public string Prefab = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            bool marked = await pair.Host.Run(() => Owned.SetFixture(o.Prefab, true));
            if (marked) pair.Cleanup(() => pair.Host.Run(() => Owned.SetFixture(o.Prefab, false)));

            ushort fixture = await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "client"));
            bool kept = await pair.Client.Run<bool>(s => Owned.TryStash(s, fixture), 60);
            ItemRecord record = await pair.Host.Run(() => Items.Record(fixture));
            Report.Note($"fixture {o.Prefab} {fixture}: in the client's inventory {kept}; host record {record}");
            Expect.That(record.Placement != "PlayerInventory", "the host refused the fixture into the inventory", record.ToString());
            Expect.That(!kept, "the client doesn't keep the fixture in its inventory");

            await pair.Host.Run(() => Owned.SetFixture(o.Prefab, false));
            ushort ordinary = await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "client"));
            bool stashed = await pair.Client.Run<bool>(s => Owned.TryStash(s, ordinary), 60);
            record = await pair.Host.Run(() => Items.Record(ordinary));
            Report.Note($"ordinary {o.Prefab} {ordinary}: in the client's inventory {stashed}; host record {record}");
            Expect.That(stashed && record.Placement == "PlayerInventory", "an ordinary item still stashes", record.ToString());
        }
    }
}
