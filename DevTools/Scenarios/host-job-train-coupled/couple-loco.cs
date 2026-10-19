// Host: a shunter on the job's track rolls onto a free end of the job's train and the host couples
// it the way a player does when the couplers meet (the coupling helper). The loco is spawned 30-50 m
// beyond the end; a spawn that lands on another track, or couples to something else, is deleted and
// the next tried. Args: cars (the job's car NetIds), loco (livery, default LocoDE2).
var livery = DV.Globals.G.Types.Liveries.First(l => l.id == (ctx.Args.TryGetValue("loco", out var id) ? id : "LocoDE2"));
var jobCars = new List<TrainCar>();
foreach (string s in ctx.Args["cars"].Split(',').Distinct())
    if (NetworkedTrainCar.TryGet(ushort.Parse(s), out TrainCar c) && c != null)
        jobCars.Add(c);
var ends = new List<(TrainCar car, Coupler coupler)>();
foreach (TrainCar car in jobCars)
    foreach (Coupler cp in new[] { car.frontCoupler, car.rearCoupler })
        if (!cp.IsCoupled())
            ends.Add((car, cp));
var tried = new List<string>();
foreach (var (endCar, end) in ends)
foreach (float distance in new[] { 30f, 50f })
{
    Vector3 outward = Vector3.ProjectOnPlane(end.transform.position - endCar.transform.position, Vector3.up).normalized;
    TrainCar loco = CarSpawner.Instance.SpawnCarOnClosestTrack(end.transform.position + outward * distance, livery, false, true, false);
    if (loco == null) { tried.Add($"{endCar.ID} +{distance} m: no room"); continue; }
    for (int i = 0; i < 60; i++) yield return null;
    var endTracks = new HashSet<RailTrack>(endCar.Bogies.Select(b => b.track));
    if (!loco.Bogies.Any(b => endTracks.Contains(b.track)) || loco.trainset.cars.Count > 1)
    {
        tried.Add($"{endCar.ID} +{distance} m: landed on {loco.Bogies[0].track?.name} with {loco.trainset.cars.Count} car(s)");
        Trains.Delete(loco);
        for (int i = 0; i < 30; i++) yield return null;
        continue;
    }

    Coupler near = new[] { loco.frontCoupler, loco.rearCoupler }
        .OrderBy(c => Vector3.Distance(c.transform.position, end.transform.position)).First();
    foreach (TrainCar car in jobCars) car.brakeSystem.SetHandbrakePosition(1f);
    loco.brakeSystem.SetHandbrakePosition(0f);
    float closest = float.MaxValue;
    string how = "none";
    for (float t0 = Time.time; Time.time - t0 < 60f;)
    {
        yield return new WaitForFixedUpdate();
        float gap = Vector3.Distance(near.transform.position, end.transform.position);
        closest = Mathf.Min(closest, gap);
        if (near.coupledTo == end) { how = "on contact"; break; }
        if (gap < 0.9f)
        {
            new DV.HUD.UICouplingHelper { trainCar = endCar, shouldAutoHandbrake = () => false }.HandleCoupling(end, false);
            how = "coupling helper";
            break;
        }
        Vector3 toward = Vector3.ProjectOnPlane(end.transform.position - near.transform.position, Vector3.up);
        Vector3 dir = loco.transform.forward * Mathf.Sign(Vector3.Dot(loco.transform.forward, toward));
        float v = gap > 5f ? 2f : 0.6f;
        loco.rb.AddForce(dir * v - Vector3.Project(loco.rb.velocity, dir), ForceMode.VelocityChange);
    }
    for (int i = 0; i < 50; i++) { yield return new WaitForFixedUpdate(); loco.rb.velocity = Vector3.zero; }
    loco.brakeSystem.SetHandbrakePosition(1f);
    for (int i = 0; i < 60; i++) yield return null;
    if (near.coupledTo == end)
    {
        NetworkedTrainCar.TryGetNetId(loco, out ushort locoNetId);
        ctx.Result = new { loco = loco.ID, locoNetId = (int)locoNetId, end = endCar.ID, how, closest = Math.Round(closest, 2) };
        yield break;
    }
    tried.Add($"{endCar.ID} +{distance} m: rolled to {closest:F2} m, not coupled ({how})");
    Trains.Delete(loco);
    for (int i = 0; i < 30; i++) yield return null;
}
throw new Exception("couldn't couple a " + livery.id + " to the job's train: " + string.Join("; ", tried));
