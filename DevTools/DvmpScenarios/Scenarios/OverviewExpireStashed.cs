using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Shared.Jobs;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A stashed overview whose job expires (docs/design/items/inventory.md, "Job papers"; R4C C9
    /// B1). The client takes a job's overview off FM's table and stashes it; the host expires every
    /// job offered at the station (StationController.ExpireAllAvailableJobsInStation, as DV does when
    /// the players leave). The overview, a row no job wants now, is made and destroyed, which left its
    /// job dirty for an overview that was gone: the station's update threw on it every tick and sent
    /// no job update again. The host logs no error or exception, the client's slot is empty, and a
    /// job taken at FM afterwards (once the players have been away and DV has made new ones) reaches
    /// the client: its booklet appears there.
    /// </summary>
    [Scenario("overview-expire-stashed", "A stashed overview whose job expires goes from the client's slot, the host logs no error, and the station's next job reaches the client")]
    [Budget(420)]
    public sealed class OverviewExpireStashed : Scenario<OverviewExpireStashed.Options>
    {
        public sealed class Options
        {
            public string Station = "FM";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string job = await AtOffice(pair, o.Station);
            ushort overview = await pair.Host.Run(() => InGame.OverviewOf(job));
            Require.That(overview != 0, $"{job} has an overview on the table");
            int slot = await pair.Client.Run<int>(s => Items.Stash(s, overview, false), 60);
            Report.Note($"the client stashed {job}'s overview {overview} in slot {slot}");
            Require.That(await pair.Client.Run(() => PlayerInventory.IsStandIn(overview)), "the stashed overview is a stand-in");

            long cursor = await pair.Host.LogCursor();
            int expired = await pair.Host.Run(() => InGame.ExpireAll(o.Station));
            Report.Note($"the host expired {expired} job(s) at {o.Station}");
            await Task.Delay(5000);   // the paper goes, and the station runs its updates
            LogLine[] errors = await pair.Host.LogsSince(cursor, "error");
            Expect.Empty(errors.Select(l => l.Message), "errors and exceptions in the host's log since the expiry (B1)");
            Expect.Equal("", await pair.Client.Run(() => InGame.InSlot(slot)), $"what's in the client's slot {slot} after its overview's job expired");
            Expect.That(!await pair.Client.Run(() => PlayerInventory.Holds(overview)), "the client no longer holds the overview");

            Report.Note($"both players away so {o.Station} makes new jobs, and back");
            string next = await AtOffice(pair, o.Station, true);
            pair.Cleanup(() => pair.Host.Run(() => BookletSummon.InGame.Abandon(next)));
            ushort booklet = await pair.Client.Run<ushort>(s => BookletSummon.InGame.Accept(s, next), 60);
            Report.Note($"the client took {next}: booklet {booklet}");
            Expect.That(booklet != 0, $"the client has {next}'s booklet: the station's job updates reach it");
        }

        /// <summary>
        /// Both players at the station's office with a transport or empty-haul job offered there, as
        /// booklet-summon: if there's none (or <paramref name="away"/>), both go 2.6 km away first so
        /// DV makes new ones. Result: the job.
        /// </summary>
        public static async Task<string> AtOffice(Pair pair, string station, bool away = false)
        {
            Peer[] players = pair.Dedicated ? new[] { pair.Client } : new[] { pair.Host, pair.Client };
            if (away || await pair.Host.Run(() => BookletSummon.InGame.AvailableJobs(station)) == 0)
            {
                if (!away) Report.Note($"no transport or empty-haul job at {station}: going 2 km away so DV generates new ones");
                foreach (Peer p in players) await p.Run(() => Players.TeleportToGround(Places.AwayX, Places.AwayZ), 90);
                await Task.Delay(8000);   // the station notices everyone has gone
            }
            for (int i = 0; i < players.Length; i++)
            {
                float x = Places.OfficeX - 4f * i;
                await players[i].Run(() => Players.TeleportToGround(x, Places.OfficeZ), 90);
            }
            await Task.Delay(6000);   // the office's overviews load and reach the client
            return await pair.Host.Run(() => BookletSummon.InGame.PickJob(station));
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the NetId of the job's overview (an object, or a row in an inventory), 0 for none.</summary>
            [Step]
            public static ushort OverviewOf(string jobId) =>
                NetworkedJob.TryGetFromJobId(jobId, out NetworkedJob job) ? job.JobOverviewNetId : (ushort)0;

            /// <summary>Host: expires every job offered at the station, as DV does when its players have
            /// gone. Result: how many were offered.</summary>
            [Step]
            public static int ExpireAll(string station)
            {
                StationController controller = StationController.allStations.First(s => s.stationInfo.YardID == station);
                int offered = controller.logicStation.availableJobs.Count;
                controller.ExpireAllAvailableJobsInStation();
                return offered;
            }

            /// <summary>This player: what's in the inventory slot, a silhouette too ("" for nothing).</summary>
            [Step]
            public static string InSlot(int slot)
            {
                UnityEngine.GameObject go = DV.InventorySystem.Inventory.Instance.PeekItemAtSlot(slot, true);
                return go == null ? "" : $"{PlayerInventory.PrefabOf(go)} {PlayerInventory.NetIdOf(go)}";
            }
        }
    }
}
