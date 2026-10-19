// Host: clear payable debts (vanilla refuses jobs with them) and pick an offered job at the station
// that can be re-rolled: its overview lies where it was printed, and every one of its cars is
// there and nobody is on them. Args: station (yard ID), except (a job's NetId to
// skip, optional).
var debts = DV.ServicePenalty.CareerManagerDebtController.Instance;
debts.ClearRestOfThePayableDebts(); debts.RefreshExistingDebtsState();
var st = StationController.allStations.First(s => s.stationInfo.YardID == ctx.Args["station"]);
string except = ctx.Args.TryGetValue("except", out var e) ? e : "";
var registry = TrainCarRegistry.Instance.logicCarToTrainCar;
var players = NetworkLifecycle.Instance.Server.ServerPlayers.Select(p => p.CarId).ToHashSet();
foreach (var job in st.logicStation.availableJobs.ToList())
{
    if (!NetworkedJob.TryGetFromJob(job, out var nj) || nj.NetId.ToString() == except ||
        nj.JobOverview == null || nj.JobOverview.GetItemState() != ItemState.Dropped)
        continue;
    if (!JobsManager.Instance.jobToJobCars.TryGetValue(job, out var logicCars) || logicCars.Count == 0)
        continue;
    var cars = logicCars.Select(c => registry.TryGetValue(c, out var tc) ? tc : null).ToList();
    if (cars.Any(tc => tc == null || !NetworkedTrainCar.TryGetFromTrainCar(tc, out var n) || n.HasPlayers || players.Contains(n.NetId)))
        continue;
    return new
    {
        job = nj.NetId, jobId = job.ID, overview = nj.JobOverview.NetId,
        // "ID:NetId" for each car: an ID alone can come back on a new car
        cars = string.Join(",", cars.Select(tc => tc.ID + ":" + tc.GetComponent<NetworkedTrainCar>().NetId)),
        firstCar = cars[0].ID,
        offered = st.logicStation.availableJobs.Count, capacity = st.proceduralJobsRuleset.jobsCapacity
    };
}
throw new Exception("no offered job at " + ctx.Args["station"] + " that can be re-rolled (" +
                    st.logicStation.availableJobs.Count + " offered)");
