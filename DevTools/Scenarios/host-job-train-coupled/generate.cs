// Host: the station's job generator makes fresh jobs and spawns their trains, as when a player
// arrives at a station: its available jobs are expired, then StationProceduralJobsController
// generates new ones. Waits for a new transport or empty-haul job whose overview is printed, and
// returns it with the NetIds of its cars. Args: station (yard ID).
var debts = DV.ServicePenalty.CareerManagerDebtController.Instance;
debts.ClearRestOfThePayableDebts(); debts.RefreshExistingDebtsState();
var st = StationController.allStations.First(s => s.stationInfo.YardID == ctx.Args["station"]);
var old = new HashSet<Job>(st.logicStation.availableJobs.Concat(st.logicStation.takenJobs));
st.ExpireAllAvailableJobsInStation();
yield return new WaitForSeconds(1f);
st.ProceduralJobsController.TryToGenerateJobs();
bool Ready(Job j) => !old.Contains(j) &&
    (j.jobType == DV.ThingTypes.JobType.Transport || j.jobType == DV.ThingTypes.JobType.EmptyHaul) &&
    NetworkedJob.TryGetFromJob(j, out NetworkedJob n) && n.JobOverview != null && n.JobOverview.GetItemState() == ItemState.Dropped;
Job job = null;
for (float t0 = Time.realtimeSinceStartup; job == null && Time.realtimeSinceStartup - t0 < 60f;)
{
    yield return new WaitForSeconds(0.5f);
    job = st.logicStation.availableJobs.FirstOrDefault(Ready);
}
if (job == null)
    throw new Exception($"{ctx.Args["station"]} generated no transport or empty-haul job with a printed overview in 60 s " +
                        $"({st.logicStation.availableJobs.Count(j => !old.Contains(j))} new job(s): " +
                        string.Join(", ", st.logicStation.availableJobs.Where(j => !old.Contains(j)).Select(j => j.jobType)) + ")");
NetworkedJob.TryGetFromJob(job, out NetworkedJob networkedJob);
List<ushort> cars = JobData.FromJob(networkedJob).GetCars();
// Let the cars settle and the job reach the client.
yield return new WaitForSeconds(3f);
ctx.Result = new { job = job.ID, type = job.jobType.ToString(), cars = string.Join(",", cars) };
