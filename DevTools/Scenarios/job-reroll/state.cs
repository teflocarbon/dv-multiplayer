// Either peer: a job's state and overview, which of its cars still exist, and the station's offered
// jobs. Args: job (the job's NetId), cars ("ID:NetId,..."), station (yard ID).
var nj = Resources.FindObjectsOfTypeAll<NetworkedJob>().FirstOrDefault(n => n != null && n.NetId.ToString() == ctx.Args["job"]);
var job = nj?.Job;
var ov = nj?.JobOverview;
bool hasOverview = ov != null && ov.gameObject.activeInHierarchy;
var wanted = ctx.Args["cars"].Split(',');
var live = UnityEngine.Object.FindObjectsOfType<NetworkedTrainCar>()
    .Where(n => n.TrainCar != null && wanted.Contains(n.TrainCar.ID + ":" + n.NetId)).Select(n => n.TrainCar.ID).ToList();
var st = StationController.allStations.First(s => s.stationInfo.YardID == ctx.Args["station"]);
return new
{
    state = job == null ? "gone" : job.State.ToString(),
    overview = hasOverview ? ov.NetId + ":" + ov.GetItemState() : "none",
    overviewAt = hasOverview ? Dev.Abs(ov.transform.position).ToString("F2") : "",
    cars = string.Join(",", live), carsLeft = live.Count,
    offered = string.Join(",", st.logicStation.availableJobs.Select(j => j.ID).OrderBy(id => id)),
    offeredCount = st.logicStation.availableJobs.Count,
    generating = st.ProceduralJobsController.IsJobGenerationActive
};
