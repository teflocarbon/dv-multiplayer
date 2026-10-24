using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using DV.Booklets;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// An expired-job report in a slot (docs/design/items/inventory.md, "Putting away"; R4C C9 A8).
    /// It has no recipe a row could make it from, so a stand-in for one was made again at a take-out
    /// as a blank generic copy, with no job on it. The host prints one (BookletCreator, as the
    /// validator does for an expired job's booklet) beside the client, which stashes it: it stays an
    /// object in the slot, not a stand-in, and taken out it names the same job as before. On a player
    /// host, the host's own player does the same with another.
    /// </summary>
    [Scenario("expired-report-slot", "An expired-job report stays an object in a slot, on a client and a player host, and names its job after a take-out")]
    [Budget(180)]
    public sealed class ExpiredReportSlot : Scenario<ExpiredReportSlot.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            Peer[] players = pair.Dedicated ? new[] { pair.Client } : new[] { pair.Client, pair.Host };
            foreach (Peer who in players)
            {
                Printed report = await pair.Host.Run<Printed>(s => InGame.Print(s, "expired", who.Name));
                Report.Note($"an expired report {report.NetId} for {report.JobId} beside the {who.Name}");
                Kept k = await who.Run<Kept>(s => InGame.StashAndTakeOut(s, report.NetId), 90);
                Report.Note($"{who.Name}: {k}");
                Expect.That(k.Slot >= 0, $"{who.Name}: the expired report went into a slot");
                Expect.That(!k.StandIn, $"{who.Name}: the expired report is no stand-in in its slot (A8)");
                Expect.That(k.Object, $"{who.Name}: the expired report's object is in its slot");
                Expect.Equal(k.Before, k.After, $"{who.Name}: the job the expired report names after the take-out");
                if (who == pair.Host)
                    Expect.Equal(report.JobId, k.After, "host: the job its own expired report names after the take-out");
            }
        }

        public sealed class Printed
        {
            public ushort NetId;
            public string JobId = "";
        }

        public sealed class Kept
        {
            public int Slot = -1;
            public bool StandIn, Object;
            public string Before = "", After = "";
            public override string ToString() =>
                $"slot {Slot}{(StandIn ? " (stand-in)" : "")}{(Object ? ", an object" : ", no object")}; names '{Before}', after the take-out '{After}'";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: a job paper printed beside a player (<paramref name="near"/> "host" or "client")
            /// for a job the station system has: <paramref name="kind"/> "expired" (an expired-job
            /// report) or "report" (a job report, with its render recipe). The run removes it after.
            /// Result: its NetId and job.
            /// </summary>
            [Step]
            public static IEnumerator Print(Step<Printed> step, string kind, string near)
            {
                Vector3 at = near == "host"
                    ? PlayerManager.PlayerTransform.position
                    : UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First().transform.position;
                at += Vector3.up * 0.6f + Vector3.right * 0.8f;
                NetworkedJob job = NetworkedJob.GetAllJobs().FirstOrDefault(j => j.Job != null && j.Job.State == DV.ThingTypes.JobState.Available)
                                   ?? NetworkedJob.GetAllJobs().FirstOrDefault(j => j.Job != null)
                                   ?? throw new Exception("no job to print a paper for");
                Component paper = kind switch
                {
                    "expired" => BookletCreator.CreateJobExpiredReport(job.Job, at, Quaternion.identity, WorldMover.OriginShiftParent),
                    "report" => BookletCreator.CreateJobReport(job.Job, null, at, Quaternion.identity, WorldMover.OriginShiftParent),
                    _ => throw new ArgumentException("kind: expired or report, not " + kind)
                };
                NetworkedItem item = paper.GetComponent<NetworkedItem>();
                yield return Wait.Until(() => item != null && item.NetId != 0, 5f, $"the {kind} to get a net ID");
                Items.Track(item.NetId);
                for (int i = 0; i < 60; i++) yield return null;
                step.Result = new Printed { NetId = item.NetId, JobId = job.Job.ID };
            }

            /// <summary>
            /// This player: picks the expired report up and stashes it, and reads it in its slot; then
            /// takes it out and drops it. Result: its slot, what it is there, and the job it names
            /// before and after.
            /// </summary>
            [Step]
            public static IEnumerator StashAndTakeOut(Step<Kept> step, ushort id)
            {
                var kept = new Kept();
                step.Result = kept;
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                kept.Before = item.GetComponent<JobExpiredReport>()?.jobId ?? "";
                DV.InventorySystem.Inventory.Instance.AddItemToInventory(item.gameObject);
                yield return Wait.Until(() => PlayerInventory.Holds(id), 5f, "the stash");
                yield return new WaitForSecondsRealtime(1.5f);
                kept.Slot = PlayerInventory.Slot(id);
                kept.StandIn = PlayerInventory.IsStandIn(id);
                kept.Object = NetworkedItem.TryGet(id, out NetworkedItem stored) && stored != null;
                yield return PlayerInventory.TakeOut(id);
                for (int i = 0; i < 30; i++) yield return null;
                kept.After = NetworkedItem.TryGet(id, out NetworkedItem taken) && taken != null
                    ? taken.GetComponent<JobExpiredReport>()?.jobId ?? "" : "(no object)";
                yield return Hands.Drop();
                Hands.ReleaseControl();
            }
        }
    }
}
