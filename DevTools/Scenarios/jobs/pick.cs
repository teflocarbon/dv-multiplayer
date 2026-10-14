// Host: clear payable debts (test saves pile them up and vanilla then refuses jobs) and pick an
// available transport or empty-haul job at the station whose overview is lying ready. Args: station (yard ID).
var c = DV.ServicePenalty.CareerManagerDebtController.Instance;
c.ClearRestOfThePayableDebts(); c.RefreshExistingDebtsState();
var st = StationController.allStations.First(s => s.stationInfo.YardID == ctx.Args["station"]);
var job = st.logicStation.availableJobs.Where(j => j.jobType == DV.ThingTypes.JobType.Transport || j.jobType == DV.ThingTypes.JobType.EmptyHaul)
    .FirstOrDefault(j => NetworkedJob.TryGetFromJob(j, out var n) && n.JobOverview != null && n.JobOverview.GetItemState() == ItemState.Dropped);
if (job == null) throw new Exception("no available transport or empty-haul job with an overview at " + ctx.Args["station"]);
return new { job = job.ID, pay = job.GetBasePaymentForTheJob(), allowed = c.IsPlayerAllowedToTakeJob() };
