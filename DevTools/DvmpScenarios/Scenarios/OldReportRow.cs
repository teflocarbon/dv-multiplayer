using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Jobs;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A job report's row that can't go live at a join stays cold (docs/design/items/inventory.md,
    /// "Job papers"; R4C C9 B4, B6). A report has no job to print it again, so its row is never
    /// dropped. The host prints a job report beside the client, which stashes it, then:
    /// - default (B4): the host saves with the report's recipe (<c>mpJobReport</c>) taken out of its
    ///   row, as a save from before C7 has it, and both games restart. The row was taken up into its
    ///   slot and every take-out failed (item-data-unavailable); now it stays in the cold store, the
    ///   client has no stand-in for it, and the host logs that it stays cold.
    /// - UNSLOTTED=1 (B6): the report is the client's own. The client leaves, and while it's away
    ///   its row loses its slot to Lost and Found, as an identity merge leaves one
    ///   (leave-full-inventory). At the rejoin the row was dropped ("goes:"); now it stays cold, and the
    ///   host logs that it stays cold.
    /// The row is forgotten afterwards.
    /// </summary>
    [Scenario("old-report-row", "A job report's row saved with no recipe (UNSLOTTED=1: with no slot) stays cold at the join instead of going live or being dropped")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class OldReportRow : Scenario<OldReportRow.Options>
    {
        public sealed class Options
        {
            /// <summary>The report is the client's, and loses its slot while the client is away (B6).</summary>
            public bool Unslotted;
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            ExpiredReportSlot.Printed report = await pair.Host.Run<ExpiredReportSlot.Printed>(s => ExpiredReportSlot.InGame.Print(s, "report", "client"));
            if (o.Unslotted) await pair.Host.Run(() => Ownership.Give(report.NetId, "client"));
            int slot = await pair.Client.Run<int>(s => Items.Stash(s, report.NetId, false), 60);
            Require.That(await pair.Client.Run(() => PlayerInventory.IsStandIn(report.NetId)), "the client's report is a stand-in in its slot");
            string id = await pair.Host.Run(() => Owned.IdOf(report.NetId));
            Report.Note($"the client stashed a report {report.NetId} ({id}) for {report.JobId} in slot {slot}");
            pair.Cleanup(() => pair.Host.Run(() => InGame.Forget(id)));

            long cursor = 0;
            if (o.Unslotted)
            {
                await pair.Client.Quit();
                await Until.Value(() => pair.Host.Run(() => Ownership.RemotePlayers()), n => n == 0, 60, "the host to notice the client left");
                await Task.Delay(3000);   // its departure is handled
                Require.Equal("cold", await pair.Host.Run(() => ColdHomes.InGame.State(id)), "the report is a cold row while the client is away");
                Report.Note("unslotted: " + await pair.Host.Run(() => LeaveFullInventory.InGame.Unslot(id)));
                cursor = await pair.Host.LogCursor();
                await pair.Rejoin();
                await Task.Delay(15000);   // the client receives its items
            }
            else
            {
                string edited = await pair.Host.Run<string>(s => InGame.SaveWithoutRecipe(s, id), 60);
                Report.Note("saved without its recipe: " + edited);
                Require.That(edited.Length > 0, "the report's saved row had its recipe taken out", edited);
                await pair.Restart(o.Session);
                await Task.Delay(20000);   // the client receives its items
            }

            Expect.Equal("cold", await pair.Host.Run(() => ColdHomes.InGame.State(id)), "the report's row after the join (in the cold store, not live)");
            Expect.Equal(-1, await pair.Client.Run(() => InGame.ReportSlot(report.JobId)), $"the client's slot with a report for {report.JobId} after the join");
            LogLine[] lines = await pair.Host.LogsSince(cursor);
            Expect.That(lines.Any(l => l.Message.Contains(id) && l.Message.Contains("stays cold")), "the host logs that the report stays cold");
            Expect.Empty(lines.Where(l => l.Message.Contains(id) && l.Message.Contains(" goes: ")).Select(l => l.Message), "lines dropping the report's row");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: a manual save with the report's recipe taken out of its saved row, wherever
            /// the row is under <c>Multiplayer.Items</c>. Result: the row's id, "" if no row had one.</summary>
            [Step]
            public static IEnumerator SaveWithoutRecipe(Step<string> step, string id) => SaveEdits.SaveWith(step, root =>
            {
                JObject row = root["Items"]?.SelectTokens("$..[?(@.id == '" + id + "')]").OfType<JObject>()
                    .FirstOrDefault(r => r["state"]?["mpJobReport"] != null);
                if (row == null) return "";
                ((JObject)row["state"]).Remove("mpJobReport");
                return (string)row["id"];
            });

            /// <summary>This player: the inventory slot of a job report for the job (a stand-in or an object), or -1.</summary>
            [Step]
            public static int ReportSlot(string jobId)
            {
                foreach ((ushort _, int slot, GameObject item) in PlayerInventory.Stored())
                {
                    if (item.GetComponent<JobReport>()?.jobID == jobId)
                        return slot;
                    if (item.TryGetComponent(out InventoryStandIn standIn) && standIn.PrefabName == nameof(JobReport) &&
                        standIn.State.Length > 0 && JobPapers.NameOf(JObject.Parse(standIn.State)) == jobId)
                        return slot;
                }
                return -1;
            }

            /// <summary>Host, cleanup: the report's cold row and its ledger entry go.</summary>
            [Step]
            public static void Forget(string id)
            {
                if (!Guid.TryParse(id, out Guid item)) return;
                ColdInventories.Remove(item);
                if (ServerLedger.Ledger.IsLive(item))
                    ServerLedger.Ledger.End(item, Multiplayer.Core.Items.ChangeCauses.ItemRemovalReason.DestroyedByGame, detail: "old-report-row");
            }
        }
    }
}
