// Host: the station's job generator makes fresh jobs and spawns their trains, as when a player
// arrives at a station: its available jobs are expired, then StationProceduralJobsController
// generates new ones. Picks a new job with cars, whose overview is printed and which the career is
// licensed for: a transport or empty-haul one if there is one, else any (a shunting job's cars are
// a job train too). Up to three rounds of generation. Returns the job with the NetIds of its cars.
// Args: station (yard ID).
var debts = DV.ServicePenalty.CareerManagerDebtController.Instance;
debts.ClearRestOfThePayableDebts(); debts.RefreshExistingDebtsState();
var st = StationController.allStations.First(s => s.stationInfo.YardID == ctx.Args["station"]);
var seen = new HashSet<Job>(st.logicStation.availableJobs.Concat(st.logicStation.takenJobs));
bool Licensed(Job j) => LicenseManager.Instance.IsLicensedForJob(DV.ThingTypes.JobLicenseType_v2.ToV2List(j.requiredLicenses));
bool Preferred(Job j) => j.jobType == DV.ThingTypes.JobType.Transport || j.jobType == DV.ThingTypes.JobType.EmptyHaul;
List<ushort> Cars(Job j) => NetworkedJob.TryGetFromJob(j, out NetworkedJob n) ? JobData.FromJob(n).GetCars() : new List<ushort>();
bool Ready(Job j) => !seen.Contains(j) && Licensed(j) &&
    NetworkedJob.TryGetFromJob(j, out NetworkedJob n) && n.JobOverview != null && n.JobOverview.GetItemState() == ItemState.Dropped &&
    Cars(j).Count > 0;
Job job = null;
var rounds = new List<string>();
for (int round = 1; job == null && round <= 3; round++)
{
    st.ExpireAllAvailableJobsInStation();
    yield return new WaitForSeconds(1f);
    st.ProceduralJobsController.TryToGenerateJobs();
    float idleSince = -1f;
    for (float t0 = Time.realtimeSinceStartup; job == null && Time.realtimeSinceStartup - t0 < 60f;)
    {
        yield return new WaitForSeconds(0.5f);
        var ready = st.logicStation.availableJobs.Where(Ready).ToList();
        job = ready.FirstOrDefault(Preferred);
        // Once generation has finished and the overviews have had time to print, take any.
        if (st.ProceduralJobsController.IsJobGenerationActive) idleSince = -1f;
        else if (idleSince < 0f) idleSince = Time.realtimeSinceStartup;
        if (job == null && idleSince >= 0f && Time.realtimeSinceStartup - idleSince > 4f)
            job = ready.FirstOrDefault();
    }
    var fresh = st.logicStation.availableJobs.Where(j => !seen.Contains(j)).ToList();
    rounds.Add($"round {round}: " + (fresh.Count == 0 ? "no new jobs" :
        string.Join(", ", fresh.Select(j => j.jobType + (Licensed(j) ? "" : " (not licensed)") + (Cars(j).Count == 0 ? " (no cars)" : "")))));
    foreach (Job j in fresh) seen.Add(j);
}
if (job == null)
    throw new Exception($"{ctx.Args["station"]} generated no job to take: " + string.Join("; ", rounds));
List<ushort> cars = Cars(job);
// Let the cars settle and the job reach the client.
yield return new WaitForSeconds(3f);
ctx.Result = new { job = job.ID, type = job.jobType.ToString(), cars = string.Join(",", cars), rounds = string.Join("; ", rounds) };
