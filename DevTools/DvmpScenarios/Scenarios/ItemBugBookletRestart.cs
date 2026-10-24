using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Known bug OI-COLD-15 (R4C, with OI-COLD-07): a client's job booklet isn't back in their own
    /// inventory after a restart. The mod saves no job paperwork, and at the load vanilla prints a
    /// booklet for each taken job into the local player's inventory: the host's on a player host,
    /// nobody's on a dedicated host (docs/jobs.md, "Save, load and rejoin"). The client takes a
    /// job at FM's office and stashes its booklet, the host saves, and both games restart: the
    /// booklet must be in the client's inventory, in its slot. The run notes where it is instead.
    /// </summary>
    [Scenario("item-bug-booklet-restart", "Known bug OI-COLD-15: a client's job booklet is back in their own inventory, in its slot, after a restart")]
    [KnownBug("OI-COLD-15")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class ItemBugBookletRestart : Scenario<ItemBugBookletRestart.Options>
    {
        public sealed class Options
        {
            public string Station = "FM";
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
        }

        // FM's office, as booklet-summon.
        private const float OfficeX = 5990f, OfficeZ = 6740f, AwayX = 8600f, AwayZ = 6740f;

        protected override async Task Run(Pair pair, Options o)
        {
            Peer[] players = pair.Dedicated ? new[] { pair.Client } : new[] { pair.Host, pair.Client };
            if (await pair.Host.Run(() => BookletSummon.InGame.AvailableJobs(o.Station)) == 0)
            {
                Report.Note($"no transport or empty-haul job at {o.Station}: going 2 km away so DV generates new ones");
                foreach (Peer p in players) await p.Run(() => Players.TeleportToGround(AwayX, AwayZ), 90);
                await Task.Delay(8000);
            }
            for (int i = 0; i < players.Length; i++)
            {
                float x = OfficeX - 4f * i;
                await players[i].Run(() => Players.TeleportToGround(x, OfficeZ), 90);
            }
            await Task.Delay(6000);   // the office's overviews load and reach the client

            string job = await pair.Host.Run(() => BookletSummon.InGame.PickJob(o.Station));
            Report.Note($"the client takes {job}");
            pair.Cleanup(() => pair.Host.Run(() => BookletSummon.InGame.Abandon(job)));
            ushort booklet = await pair.Client.Run<ushort>(s => BookletSummon.InGame.Accept(s, job), 60);
            int slot = await pair.Client.Run<int>(s => Items.Stash(s, booklet, false), 60);
            Report.Note($"the client stashed booklet {booklet} in slot {slot}");
            Require.Equal(slot, await pair.Client.Run(() => InGame.SlotOf(job)), "the client's booklet slot before the save");

            await pair.Host.Run(() => Saves.Manual());
            await pair.Restart(o.Session);
            await Task.Delay(20000);   // the client receives its items; vanilla's reprint waits for the host's

            int client = await pair.Client.Run(() => InGame.SlotOf(job));
            int host = pair.Dedicated ? -1 : await pair.Host.Run(() => InGame.SlotOf(job));
            Report.Note($"after the restart: the client's inventory {(client < 0 ? "has no booklet for it" : "slot " + client)}; " +
                        (pair.Dedicated ? "a dedicated host prints none" : $"the host's inventory {(host < 0 ? "has none" : "slot " + host)}"));
            Expect.Equal(slot, client, $"the booklet for {job} in the client's inventory after the restart");
            if (!pair.Dedicated)
                Expect.Equal(-1, host, $"the booklet for {job} in the host's inventory after the restart");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>This player: the inventory slot of a booklet for the job, or -1.</summary>
            [Step]
            public static int SlotOf(string jobId)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                for (int s = 0; s < inv.Capacity; s++)
                {
                    GameObject item = inv.PeekItemAtSlot(s, false);
                    if (item != null && item.GetComponent<JobBooklet>()?.job?.ID == jobId)
                        return s;
                }
                return -1;
            }
        }
    }
}
