using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Jobs;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A stashed overview through a rejoin (docs/design/items/inventory.md, "Job papers"; R4C C9 B2,
    /// B3). The client stashes a job's overview at FM and leaves: its row goes cold, and every job
    /// forgets its NetId (JobPaperRows.Forget), so no job's overview or booklets still name it. Its
    /// job kept it by the freed NetId, so at the rejoin the row wasn't wanted (its job had an
    /// overview, of no object) and went. After the rejoin the overview is back in its slot as a
    /// stand-in, and the job's overview is its new NetId. The client drops it afterwards. On a dedicated
    /// host nobody stays near the station, so DV expires its jobs when the client leaves, and the
    /// overview goes at the rejoin instead.
    /// </summary>
    [Scenario("overview-rejoin", "A stashed overview's NetId is forgotten by every job when its holder leaves, and the overview is back in its slot, its job's, after the rejoin")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class OverviewRejoin : Scenario<OverviewRejoin.Options>
    {
        public sealed class Options
        {
            public string Station = "FM";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            string job = await OverviewExpireStashed.AtOffice(pair, o.Station);
            ushort overview = await pair.Host.Run(() => OverviewExpireStashed.InGame.OverviewOf(job));
            Require.That(overview != 0, $"{job} has an overview on the table");
            int slot = await pair.Client.Run<int>(s => Items.Stash(s, overview, false), 60);
            Report.Note($"the client stashed {job}'s overview {overview} in slot {slot}");
            Require.Equal(overview, await pair.Host.Run(() => OverviewExpireStashed.InGame.OverviewOf(job)), "the job's overview NetId, stashed");

            await pair.Client.Quit();
            await Until.Value(() => pair.Host.Run(() => Ownership.RemotePlayers()), n => n == 0, 60, "the host to notice the client left");
            await Task.Delay(3000);   // its departure is handled
            Expect.Empty(await pair.Host.Run(() => InGame.Naming(overview)), $"jobs still naming the left overview's NetId {overview} (B2, B3)");

            await pair.Rejoin();
            await Task.Delay(15000);   // the client receives its items
            Paper back = await pair.Client.Run(() => InGame.OverviewIn(job));
            pair.Cleanup(() => back.NetId != 0 ? pair.Client.Run(() => Owned.DropFromInventory(back.NetId), 60) : Task.CompletedTask);
            Report.Note($"after the rejoin: {back}");
            if (pair.Dedicated)
            {
                // Nobody stayed near the station, so DV expired its offered jobs when the client left,
                // and an overview of an expired job goes at its holder's join (inventory.md, "Job papers").
                Expect.That(!await pair.Host.Run(() => InGame.Offered(o.Station, job)), $"{job} expired with nobody near {o.Station}");
                Expect.Equal(-1, back.Slot, $"the slot of {job}'s overview in the client's inventory after the rejoin, its job expired");
                Expect.Equal("", await pair.Client.Run(() => OverviewExpireStashed.InGame.InSlot(slot)), $"what's in the client's slot {slot} after the rejoin, its overview's job expired");
                return;
            }
            Expect.Equal(slot, back.Slot, $"the slot of {job}'s overview in the client's inventory after the rejoin");
            Expect.That(back.StandIn, "the overview is a stand-in after the rejoin");
            Expect.Equal(back.NetId, await pair.Host.Run(() => OverviewExpireStashed.InGame.OverviewOf(job)), "the job's overview NetId: the rejoined overview's");
        }

        public sealed class Paper
        {
            public ushort NetId;
            public int Slot = -1;
            public bool StandIn;
            public override string ToString() => Slot < 0 ? "none in the inventory" : $"{NetId} in slot {Slot}{(StandIn ? " (stand-in)" : "")}";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the jobs whose overview or booklets still name the NetId, as "job: what".</summary>
            [Step]
            public static string[] Naming(ushort netId)
            {
                var naming = new List<string>();
                foreach (NetworkedJob job in NetworkedJob.GetAllJobs())
                {
                    if (job.JobOverviewNetId == netId) naming.Add($"{job.Job?.ID}: its overview");
                    if (job.JobBookletNetIds.Contains(netId)) naming.Add($"{job.Job?.ID}: a booklet");
                }
                return naming.ToArray();
            }

            /// <summary>Host: whether the station still offers the job.</summary>
            [Step]
            public static bool Offered(string station, string jobId) =>
                StationController.allStations.First(s => s.stationInfo.YardID == station)
                    .logicStation.availableJobs.Any(j => j.ID == jobId);

            /// <summary>This player: the job's overview in the inventory (a stand-in or an object), with its slot.</summary>
            [Step]
            public static Paper OverviewIn(string jobId)
            {
                foreach ((ushort netId, int slot, GameObject item) in PlayerInventory.Stored())
                {
                    if (item.GetComponent<JobOverview>()?.job?.ID == jobId)
                        return new Paper { NetId = netId, Slot = slot };
                    if (item.TryGetComponent(out InventoryStandIn standIn) && standIn.PrefabName == nameof(JobOverview) &&
                        JobPapers.JobIdOf(standIn.State) == jobId)
                        return new Paper { NetId = netId, Slot = slot, StandIn = true };
                }
                return new Paper();
            }
        }
    }
}
