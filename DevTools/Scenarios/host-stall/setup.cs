// Host: a DE2 and N boxcars near the client, coupled with hoses joined and cocks open, the loco
// started and the brake pipe charged, on a stretch with at least ROOM metres of clear track on one
// side. Clear means following the switches as they're set, up to the first other car, buffer
// stop or end of the line: the push used to run the train into whatever was ahead, and the wreck
// failed the run. Args: wagons (default 4), room (metres, default 300), exclude (track names,
// comma-separated, not to use).
// Result: dir = 1 to push the loco forward, -1 to push it backward.
var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First();
DV.ThingTypes.TrainCarLivery L(string id) => DV.Globals.G.Types.Liveries.First(l => l.id == id);
int n = ctx.Args.TryGetValue("wagons", out var w) ? int.Parse(w) : 4;
float room = ctx.Args.TryGetValue("room", out var r) ? float.Parse(r) : 300f;
var exclude = new HashSet<string>(ctx.Args.TryGetValue("exclude", out var ex) ? ex.Split((char)44) : new string[0]);
var liveries = new List<DV.ThingTypes.TrainCarLivery> { L("LocoDE2") };
for (int i = 0; i < n; i++) liveries.Add(L("BoxcarBrown"));

// Metres of clear track from a point on a track in a span direction (1 = towards the out end).
float FreeAhead(RailTrack t, double span, int dir, HashSet<TrainCar> ours, float max)
{
    float dist = 0f;
    for (int hop = 0; hop < 60 && t != null; hop++)
    {
        double nearest = double.MaxValue;
        foreach (var c in CarSpawner.Instance.AllCars)
            if (c != null && !ours.Contains(c))
                foreach (var b in c.Bogies)
                    if (b.track == t && b.traveller != null)
                    {
                        double d = (b.traveller.Span - span) * dir;
                        if (d >= 0 && d < nearest) nearest = d;
                    }
        if (nearest != double.MaxValue) return Mathf.Min(max, dist + (float)nearest);
        double length = t.GetKinkedPointSet().span;
        dist += (float)(dir > 0 ? length - span : span);
        if (dist >= max) return max;
        var next = dir > 0 ? t.GetOutBranch() : t.GetInBranch();
        if (next == null || next.track == null) return dist; // buffer stop or end of the line
        t = next.track;
        dir = next.first ? 1 : -1;
        span = next.first ? 0 : t.GetKinkedPointSet().span;
    }
    return dist;
}

// Which span direction the car's forward points along its track.
int SpanDir(TrainCar car)
{
    var b = car.Bogies[0];
    var pts = b.track.GetKinkedPointSet().points.ToList();
    double s = b.traveller.Span;
    int i = Mathf.Clamp(pts.TakeWhile(p => p.span < s).Count(), 1, pts.Count - 1);
    Vector3 tangent = (Vector3)pts[i].position - (Vector3)pts[i - 1].position;
    return Vector3.Dot(car.transform.forward, tangent) >= 0 ? 1 : -1;
}

var rejected = new List<string>();
foreach (float d in new[] { 20f, 40f, 60f, 80f, 120f, 160f, 200f })
foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
{
    var t = CarSpawner.GetTrackClosestTo(client.transform.position + dir * d, 60f, out int _);
    if (t == null || exclude.Contains(t.name)) continue;
    var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(liveries, liveries.Select(_ => false).ToList(), t, true, false);
    if (cars == null) continue;
    for (int i = 0; i < 60; i++) yield return null;

    // Room from the loco, less the train's own length (the wagons may be on the side we push to).
    var ours = new HashSet<TrainCar>(cars);
    var loco = cars[0];
    float trainLength = Vector3.Distance(cars.First().transform.position, cars.Last().transform.position) + 20f;
    int sd = SpanDir(loco);
    float ahead = FreeAhead(loco.Bogies[0].track, loco.Bogies[0].traveller.Span, sd, ours, room + trainLength) - trainLength;
    float behind = FreeAhead(loco.Bogies[0].track, loco.Bogies[0].traveller.Span, -sd, ours, room + trainLength) - trainLength;
    if (Mathf.Max(ahead, behind) < room)
    {
        rejected.Add($"{t.name}: {ahead:F0}/{behind:F0} m clear");
        foreach (var c in cars) CarSpawner.Instance.DeleteCar(c);
        for (int i = 0; i < 10; i++) yield return null;
        continue;
    }
    int push = ahead >= behind ? 1 : -1;

    for (int k = 0; k < cars.Count - 1; k++)
    {
        Coupler ca = null, cb = null;
        foreach (var x in new[] { cars[k].frontCoupler, cars[k].rearCoupler }) foreach (var y in new[] { cars[k + 1].frontCoupler, cars[k + 1].rearCoupler })
            if (ca == null || Vector3.Distance(x.transform.position, y.transform.position) < Vector3.Distance(ca.transform.position, cb.transform.position)) { ca = x; cb = y; }
        if (ca.coupledTo == null) ca.CoupleTo(cb, false, false);
        ca.ConnectAirHose(cb, false); ca.IsCockOpen = true; cb.IsCockOpen = true;
    }
    // close the cocks at both ends of the train, or the open ends vent the brake pipe
    foreach (var c in cars) foreach (var end in new[] { c.frontCoupler, c.rearCoupler }) if (end.coupledTo == null) end.IsCockOpen = false;
    foreach (var c in cars) c.brakeSystem.SetHandbrakePosition(0f);
    yield return Trains.StartLoco(cars[0]);
    // cut in the loco's train brake valve (a spawned loco has it cut out), or nothing feeds the pipe
    cars[0].brakeSystem.trainBrakeCutout = true;
    // charge the brake pipe through the whole train
    float t0 = Time.time;
    while (Time.time - t0 < 120f && cars.Any(c => c.brakeSystem.brakePipePressure < 4.9f || c.brakeSystem.brakeCylinderPressure > 1.2f)) yield return null;
    ctx.Result = new { loco = cars[0].ID, cars = string.Join(",", cars.Select(c => c.ID)), last = cars.Last().ID, dir = push, track = t.name,
        clear = Math.Round(push > 0 ? ahead : behind), charged = cars.Min(c => c.brakeSystem.brakePipePressure), cylinders = cars.Max(c => c.brakeSystem.brakeCylinderPressure), seconds = Math.Round(Time.time - t0) };
    yield break;
}
throw new Exception($"no track with {room:F0} m clear near the client (tried {string.Join("; ", rejected)})");
