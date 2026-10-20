// Host: clear payable debts (vanilla refuses jobs with them) and pick an offered job at the station
// that can be re-rolled: its overview lies where it was printed, and every one of its cars is
// there with nobody on them. Prefers the job with a car nearest the office bin, and names that car:
// a player riding it stays in range of the bin's items (a car out in the yard isn't).
// Args: station (yard ID).
var debts = DV.ServicePenalty.CareerManagerDebtController.Instance;
debts.ClearRestOfThePayableDebts(); debts.RefreshExistingDebtsState();
var st = StationController.allStations.First(s => s.stationInfo.YardID == ctx.Args["station"]);
var validator = NetworkedStationController.GetFromStationController(st, out var nst) ? nst.JobValidator : null;
var bin = UnityEngine.Object.FindObjectsOfType<JobAbandoner>()
    .OrderBy(b => Vector3.Distance(b.transform.position, validator != null ? validator.transform.position : st.transform.position)).First();
var registry = TrainCarRegistry.Instance.logicCarToTrainCar;
var players = Multiplayer.Server.Session.NetworkServer.Instance.ServerPlayers.Select(p => p.CarId).ToHashSet();
var candidates = new List<(NetworkedJob nj, List<TrainCar> cars, TrainCar near, float distance)>();
foreach (var job in st.logicStation.availableJobs.ToList())
{
    if (!NetworkedJob.TryGetFromJob(job, out var nj) || nj.JobOverview == null || nj.JobOverview.GetItemState() != ItemState.Dropped)
        continue;
    if (!JobsManager.Instance.jobToJobCars.TryGetValue(job, out var logicCars) || logicCars.Count == 0)
        continue;
    var cars = logicCars.Select(c => registry.TryGetValue(c, out var tc) ? tc : null).ToList();
    if (cars.Any(tc => tc == null || !NetworkedTrainCar.TryGetFromTrainCar(tc, out var n) || n.HasPlayers || players.Contains(n.NetId)))
        continue;
    var near = cars.OrderBy(tc => Vector3.Distance(tc.transform.position, bin.transform.position)).First();
    candidates.Add((nj, cars, near, Vector3.Distance(near.transform.position, bin.transform.position)));
}
if (candidates.Count == 0)
    throw new Exception("no offered job at " + ctx.Args["station"] + " that can be re-rolled (" +
                        st.logicStation.availableJobs.Count + " offered)");
var pick = candidates.OrderBy(c => c.distance).First();
return new
{
    job = pick.nj.NetId, jobId = pick.nj.Job.ID, overview = pick.nj.JobOverview.NetId,
    // "ID:NetId" for each car: an ID alone can come back on a new car
    cars = string.Join(",", pick.cars.Select(tc => tc.ID + ":" + tc.GetComponent<NetworkedTrainCar>().NetId)),
    nearCar = pick.near.ID, nearCarMetres = (int)pick.distance,
    offered = st.logicStation.availableJobs.Count, capacity = st.proceduralJobsRuleset.jobsCapacity
};
