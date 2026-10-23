using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A job from start to finish across both players: one takes a transport (or empty haul) job's
    /// overview to the validator, the cars are delivered (their logical track set to the
    /// destination, standing in for the drive), one hands the booklet in, and the payment is
    /// banked. Both peers must agree on the job's state at every step, see the same booklet,
    /// report and banknotes, and the shared wallet must go up by the payment exactly once.
    /// </summary>
    [Scenario("jobs", "A job taken, delivered, handed in and paid across both players: the peers agree on its state at each step, and the wallet goes up by the payment once")]
    [Budget(600)]
    public sealed class JobsRoundTrip : Scenario<JobsRoundTrip.Options>
    {
        public sealed class Options
        {
            /// <summary>Who takes the job: client or host.</summary>
            public string Accept = "client";
            /// <summary>Who hands it in: client or host.</summary>
            public string Handin = "client";
            /// <summary>The station's yard ID.</summary>
            public string Station = "FM";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Peer acceptor = Places.Player(pair, o.Accept, "ACCEPT"), handler = Places.Player(pair, o.Handin, "HANDIN");

            // A job left taken at the station (test saves keep them) stops DV generating new jobs there
            // until every player has been more than 2 km from it, so without a job to take, both players
            // go 2.6 km from FM's office and come back.
            Spare spare = await pair.Host.Run(() => InGame.Spare(o.Station));
            if (spare.Offered == 0)
            {
                Report.Note($"no transport or empty-haul job at {o.Station} ({spare.Taken} taken): going 2 km away so DV generates new ones");
                await Places.Away(pair);
            }
            await Places.Office(pair, 6f);

            Picked p = await pair.Host.Run(() => InGame.Pick(o.Station));
            Report.Note($"{p.Job} (pays ${p.Pay}); {o.Accept} takes it");
            Accepted a = await acceptor.Run<Accepted>(s => Jobs.Accept(s, p.Job));
            Require.Equal("InProgress", a.State, "the job's state once taken");
            await Task.Delay(2000);
            await BothAgree(pair, p.Job, "taken");

            Delivered d = await pair.Host.Run<Delivered>(s => InGame.Deliver(s, p.Job));
            Report.Note($"cars delivered: {d.Cars} to {d.Destination}");
            Require.Equal("Done", d.Task, "the transport task once its cars are at the destination");
            double w0 = await pair.Host.Run(() => Jobs.Wallet());

            HandedIn r = await handler.Run<HandedIn>(s => InGame.Handin(s, p.Job));
            Report.Note($"{o.Handin} handed the booklet in: printed {string.Join(",", r.Printed)}, cash ${r.Cash}");
            Require.Equal("Completed", r.State, "the job's state once handed in");
            await Task.Delay(2000);
            await BothAgree(pair, p.Job, "handed in");

            string note = r.Printed.Where(s => s.StartsWith("Banknotes#")).Select(s => s.Substring(s.IndexOf('#') + 1)).FirstOrDefault();
            Require.That(note != null, "the payment was printed", string.Join(",", r.Printed));
            ushort noteId = ushort.Parse(note);
            Require.Equal(1, (await pair.Client.Run(() => Items.Existing(new[] { noteId }))).Length, "the client has the payment banknotes");

            await pair.Client.Run(() => InGame.Bank(noteId));
            await Task.Delay(2000);
            double w1 = await pair.Host.Run(() => Jobs.Wallet()), wc = await pair.Client.Run(() => Jobs.Wallet());
            Report.Note($"wallet ${w0} -> host ${w1}, client ${wc} (payment ${r.Cash})");
            Expect.That(Math.Abs(w1 - w0 - r.Cash) < 0.5, "the host's wallet went up by the payment", $"by {w1 - w0}, the payment {r.Cash}");
            Expect.That(Math.Abs(wc - w1) < 0.5, "the client's wallet matches the host's", $"client {wc}, host {w1}");
        }

        // The job's state on both peers must match.
        private static async Task BothAgree(Pair pair, string job, string when)
        {
            PeerResults<JobState> s = await pair.Both.Run(() => InGame.State(job));
            Report.Note($"host: {s.Host.State} {s.Host.Booklets} | client: {s.Client.State} {s.Client.Booklets}");
            Expect.Equal(s.Host.State, s.Client.State, $"{when}: the client's job state matches the host's");
        }

        public sealed class Spare
        {
            public int Offered, Taken;
        }

        public sealed class Picked
        {
            public string Job;
            public float Pay;
            public bool Allowed;
        }

        public sealed class Delivered
        {
            public string Task, Destination;
            public int Cars;
        }

        public sealed class HandedIn
        {
            public string State;
            /// <summary>"name#netId" of each item the hand-in printed.</summary>
            public string[] Printed;
            public double Cash;
        }

        public sealed class JobState
        {
            public string State, Booklets, Overview;
            public double Money;
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static bool Takeable(DV.Logic.Job.Job j) => j.jobType == DV.ThingTypes.JobType.Transport || j.jobType == DV.ThingTypes.JobType.EmptyHaul;

            /// <summary>Host: the station's offered transport and empty-haul jobs, and its taken ones.</summary>
            [Step]
            public static Spare Spare(string station)
            {
                var st = StationController.allStations.First(s => s.stationInfo.YardID == station);
                return new Spare { Offered = st.logicStation.availableJobs.Count(Takeable), Taken = st.logicStation.takenJobs.Count };
            }

            /// <summary>Host: clears payable debts and picks an available transport or empty-haul job at
            /// the station whose overview is lying ready.</summary>
            [Step]
            public static Picked Pick(string station)
            {
                Jobs.ClearDebts();
                var st = StationController.allStations.First(s => s.stationInfo.YardID == station);
                var job = st.logicStation.availableJobs.Where(Takeable)
                    .FirstOrDefault(j => NetworkedJob.TryGetFromJob(j, out var n) && n.JobOverview != null && n.JobOverview.GetItemState() == ItemState.Dropped)
                    ?? throw new Exception("no available transport or empty-haul job with an overview at " + station);
                return new Picked { Job = job.ID, Pay = job.GetBasePaymentForTheJob(), Allowed = DV.ServicePenalty.CareerManagerDebtController.Instance.IsPlayerAllowedToTakeJob() };
            }

            /// <summary>Host: stands in for driving a transport job's cars to their destination: each
            /// car's logical track set to the destination (what vanilla's completion check reads),
            /// handbrakes on.</summary>
            [Step]
            public static IEnumerator Deliver(Step<Delivered> step, string jobId)
            {
                var nj = Resources.FindObjectsOfTypeAll<NetworkedJob>().First(n => n.Job?.ID == jobId);
                var task = nj.Job.tasks.First();
                var data = task.GetTaskData();
                var setter = typeof(DV.Logic.Job.Car).GetProperty("CurrentTrack").GetSetMethod(true);
                foreach (var car in data.cars)
                {
                    setter.Invoke(car, new object[] { data.destinationTrack });
                    if (TrainCarRegistry.Instance.logicCarToTrainCar.TryGetValue(car, out var tc)) tc.brakeSystem.SetHandbrakePosition(1f);
                }
                for (int i = 0; i < 10; i++) yield return null;
                step.Result = new Delivered { Task = task.UpdateTaskState().ToString(), Cars = data.cars.Count, Destination = data.destinationTrack.ID.FullID };
            }

            /// <summary>Either player: picks up the job's booklet and hands it in at the nearest
            /// validator. Result: what that printed.</summary>
            [Step]
            public static IEnumerator Handin(Step<HandedIn> step, string jobId)
            {
                var nj = Resources.FindObjectsOfTypeAll<NetworkedJob>().First(n => n.Job?.ID == jobId);
                var job = nj.Job;
                var booklet = UnityEngine.Object.FindObjectsOfType<JobBooklet>().First(b => b.job == job);
                var bookletItem = booklet.GetComponent<NetworkedItem>();
                var validator = UnityEngine.Object.FindObjectsOfType<JobValidator>().OrderBy(v => Vector3.Distance(v.transform.position, PlayerManager.PlayerTransform.position)).First();
                if (Hands.Held != null && Hands.Held.NetId != bookletItem.NetId) yield return Hands.Drop();
                if (Hands.Held == null) yield return Hands.Pickup(bookletItem.NetId);
                for (int i = 0; i < 20; i++) yield return null;
                var before = new HashSet<NetworkedItem>(UnityEngine.Object.FindObjectsOfType<NetworkedItem>());
                validator.ValidateJob(booklet);
                for (float t0 = Time.realtimeSinceStartup; job.State == DV.ThingTypes.JobState.InProgress && Time.realtimeSinceStartup - t0 < 10f;) yield return null;
                for (int i = 0; i < 180; i++) yield return null;
                Hands.ReleaseControl();
                var fresh = UnityEngine.Object.FindObjectsOfType<NetworkedItem>().Where(n => !before.Contains(n) && n.NetId != 0).ToList();
                step.Result = new HandedIn
                {
                    State = job.State.ToString(),
                    Printed = fresh.Select(n => n.name.Replace("(Clone)", "") + "#" + n.NetId).ToArray(),
                    Cash = fresh.Select(n => n.GetComponent<IMoney>()).Where(m => m != null).Sum(m => m.Amount)
                };
            }

            /// <summary>Either player: picks up the banknotes and puts them in the inventory, which banks them.</summary>
            [Step]
            public static IEnumerator Bank(ushort item)
            {
                if (Hands.Held != null && Hands.Held.NetId != item) yield return Hands.Drop();
                if (Hands.Held == null) yield return Hands.Pickup(item);
                DV.InventorySystem.Inventory.Instance.AddItemToInventory(Hands.Held.gameObject);
                for (int i = 0; i < 120; i++) yield return null;
                Hands.ReleaseControl();
            }

            /// <summary>Either peer: the job's state, its booklets (net ID and state) and its overview.</summary>
            [Step]
            public static JobState State(string jobId)
            {
                var nj = Resources.FindObjectsOfTypeAll<NetworkedJob>().FirstOrDefault(n => n.Job?.ID == jobId);
                var job = nj?.Job;
                double money = DV.InventorySystem.Inventory.Instance.PlayerMoney;
                if (job == null) return new JobState { State = "gone", Booklets = "", Overview = "", Money = money };
                var booklets = UnityEngine.Object.FindObjectsOfType<JobBooklet>().Where(b => b.job == job).Select(b => b.GetComponent<NetworkedItem>()).Where(n => n != null)
                    .Select(n => n.NetId + ":" + n.GetItemState());
                var ov = nj.JobOverview;
                return new JobState { State = job.State.ToString(), Booklets = string.Join(",", booklets), Overview = ov == null ? "none" : ov.NetId + ":" + ov.GetItemState(), Money = money };
            }
        }
    }
}
