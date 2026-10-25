using System;
using System.Collections;
using System.Collections.Generic;
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
    /// A job report's row that can't go live in its slot at a join (docs/design/items/inventory.md,
    /// "Job papers"; R4C C9 B4, B6, review 4). The host prints a job report beside the client, which
    /// stashes it, then:
    /// - default (B4): the host saves with the report's recipe (<c>mpJobReport</c>) taken out of its
    ///   row, as a save from before C7 has it, and both games restart. The row was taken up into its
    ///   slot and every take-out failed (item-data-unavailable). It can never be made, so now it goes:
    ///   the cold store and the client's slots don't have it, and the host logs why.
    /// - UNSLOTTED=1 (B6): nobody owns a paper, so the report is nobody's (inventory.md, "Job
    ///   papers"). The client leaves, and while it's away the report's row loses its slot (it has no
    ///   ledger home to move, so only the row changes) and a mug of the client's goes cold into that
    ///   slot. At the rejoin the row was dropped ("goes:"); now it goes live in the lowest free slot
    ///   (<c>ServerLedger.FreeSlot</c>, as any row with no slot), a stand-in named for its job,
    ///   still nobody's and in no Lost and Found. Until 2026-10-26 this variant had the report owned
    ///   and expected it in Lost and Found; the user had it reshaped to the design: nobody owns a
    ///   paper, and Lost and Found refuses a report.
    /// - UNSLOTTED=1 FULL=1: the same, with the client's other slots filled with mugs, so at the
    ///   rejoin no slot is free. It's made from its recipe and left at the client's feet, still
    ///   nobody's, as other unowned items are (Lost and Found takes only owned ones; the user, 2026-10-25).
    /// The row and the items are forgotten afterwards.
    /// </summary>
    [Scenario("old-report-row", "A job report's row saved with no recipe goes at the join; UNSLOTTED=1: one with no slot goes live in the lowest free slot, or with FULL=1 to the player's feet")]
    [Needs(Need.RestartsGames)]
    [Budget(1500)]
    public sealed class OldReportRow : Scenario<OldReportRow.Options>
    {
        public sealed class Options
        {
            /// <summary>The report loses its slot while the client is away (B6).</summary>
            public bool Unslotted;
            /// <summary>With <see cref="Unslotted"/>: no slot is free at the rejoin.</summary>
            public bool Full;
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            ExpiredReportSlot.Printed report = await pair.Host.Run<ExpiredReportSlot.Printed>(s => ExpiredReportSlot.InGame.Print(s, "report", "client"));
            bool full = o.Unslotted && o.Full;
            int slot = await pair.Client.Run<int>(s => Items.Stash(s, report.NetId, false), 60);
            Require.That(await pair.Client.Run(() => PlayerInventory.IsStandIn(report.NetId)), "the client's report is a stand-in in its slot");
            string id = await pair.Host.Run(() => Owned.IdOf(report.NetId));
            Report.Note($"the client stashed a report {report.NetId} ({id}) for {report.JobId} in slot {slot}");
            pair.Cleanup(() => pair.Host.Run(() => InGame.Forget(id)));

            List<string> mugs = new();
            if (o.Unslotted)
            {
                Require.That(!(await pair.Host.Run(() => Owned.Entry(id))).Known, "the stashed report is nobody's");
                pair.Cleanup(async () =>
                {
                    var now = new List<ushort>();
                    foreach (string mug in mugs.Append(id))
                    {
                        ushort netId = await pair.Host.Run(() => Owned.NetIdOf(mug));
                        if (netId != 0) now.Add(netId);
                        else await pair.Host.Run(() => LeaveFullInventory.InGame.ForgetCold(mug));
                    }
                    await pair.Host.Run<ushort[]>(s => Items.Destroy(s, now.ToArray()), 120);
                });
                if (full)
                {
                    int free = LeaveFullInventory.Capacity - (await pair.Client.Run(() => LeaveFullInventory.InGame.Occupied())).Length;
                    await LeaveFullInventory.Fill(pair, mugs, free, false);
                    Require.Zero(LeaveFullInventory.Capacity - (await pair.Client.Run(() => LeaveFullInventory.InGame.Occupied())).Length, "free slots in the client's inventory");
                }
            }

            long cursor = 0;
            int lowestFree = -1;
            if (o.Unslotted)
            {
                // The report's slot gets a mug, so the lowest free slot is another (none with FULL=1).
                int[] occupied = await pair.Client.Run(() => LeaveFullInventory.InGame.Occupied());
                lowestFree = Enumerable.Range(0, LeaveFullInventory.Capacity).Where(s => !occupied.Contains(s)).DefaultIfEmpty(-1).First();
                string client = await pair.Host.Run(() => Owned.ClientIdentity());
                await pair.Client.Quit();
                await Until.Value(() => pair.Host.Run(() => Ownership.RemotePlayers()), n => n == 0, 60, "the host to notice the client left");
                await Task.Delay(3000);   // its departure is handled
                Require.Equal("cold", await pair.Host.Run(() => ColdHomes.InGame.State(id)), "the report is a cold row while the client is away");
                // Nobody's has no ledger home to move: its row loses its slot, and a mug takes it.
                int was = await pair.Host.Run(() => InGame.UnslotUnowned(id));
                mugs.Add(await pair.Host.Run<string>(s => LeaveFullInventory.InGame.ColdInto(s, client, was)));
                Report.Note($"unslotted: the report's slot {was} has a cold mug of the client's; the lowest free slot: {lowestFree}");
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

            int reportSlot = await pair.Client.Run(() => InGame.ReportSlot(report.JobId));
            LogLine[] lines = await pair.Host.LogsSince(cursor);
            string[] dropped = lines.Where(l => l.Message.Contains(id) && l.Message.Contains(" goes: ")).Select(l => l.Message).ToArray();
            if (o.Unslotted)
            {
                OwnerInfo now = await pair.Host.Run(() => Ownership.ById(id));
                Report.Note($"after the rejoin: {now}; the client's slot with the report: {reportSlot}");
                if (full)
                {
                    Expect.Equal(-1, reportSlot, $"the client's slot with a report for {report.JobId} after the join");
                    Expect.Equal("World", now.Placement, "the unowned report with no free slot, after the rejoin (at the client's feet)");
                    Expect.Equal(report.JobId, await pair.Host.Run(() => InGame.JobOf(id)), "the job the report names (made from its recipe)");
                }
                else
                {
                    Expect.Equal("PlayerInventory", now.Placement, "the report with no slot, after the rejoin (a free slot)");
                    Expect.Equal(lowestFree, reportSlot, $"the client's slot with a report for {report.JobId} (the lowest free one, named for its job)");
                    Expect.That(await pair.Client.Run(() => PlayerInventory.IsStandIn(now.NetId)), "the report is a stand-in in its slot");
                }
                Expect.That(!now.Lost, "the report is in no Lost and Found");
                Expect.That(!(await pair.Host.Run(() => Owned.Entry(id))).Known, "the report is still nobody's");
                Expect.Empty(dropped, "lines dropping the report's row");
            }
            else
            {
                Expect.Equal(-1, reportSlot, $"the client's slot with a report for {report.JobId} after the join");
                Expect.Equal("gone", await pair.Host.Run(() => ColdHomes.InGame.State(id)), "the recipe-less report's row after the join");
                Expect.That(dropped.Any(l => l.Contains("no recipe")), "the host logs that the report goes for having no recipe");
            }
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

            /// <summary>Host: the job the live report's object names, "(no object)" without one, "" if it isn't live.</summary>
            [Step]
            public static string JobOf(string id)
            {
                Guid wanted = Guid.Parse(id);
                var record = Multiplayer.Server.Items.LiveState.LiveItems.GetAll().FirstOrDefault(r => r.PersistentItemId == wanted);
                if (record == null) return "";
                return Multiplayer.Shared.Items.LiveState.NetworkedItem.TryGet(record.NetId, out var item) && item != null
                    ? item.GetComponent<JobReport>()?.jobID ?? "" : "(no object)";
            }

            /// <summary>
            /// Host: an unowned cold row loses its slot (it has no ledger entry to move, so only the row
            /// changes), leaving that slot free for another item. Result: the slot it had.
            /// </summary>
            [Step]
            public static int UnslotUnowned(string id)
            {
                if (!ColdInventories.TryGet(Guid.Parse(id), out var row))
                    throw new Exception($"{id} isn't cold");
                if (row.OwnerIdentity != Guid.Empty)
                    throw new Exception($"{id} is {row.OwnerIdentity:D}'s, not nobody's");
                int was = row.Slot;
                row.Slot = -1;
                row.ClaimSlot = -1;
                return was;
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
