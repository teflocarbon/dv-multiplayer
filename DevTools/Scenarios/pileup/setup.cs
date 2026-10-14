// Host: clears K parallel yard tracks and lays out, on each, a row of N uncoupled wagons GAP metres
// apart with a loco RUNUP metres before it (and another after it with ENDS=2). Handbrakes stay on
// until ram.cs releases them, so nothing rolls off on a grade first.
// Args: k (4), n (6), gap (3), runup (25), ends (1|2), prefix ("[Y]_[SM]_[B-"), wagon ("BoxcarBrown").
int k = ctx.Args.TryGetValue("k", out var a) ? int.Parse(a) : 4;
int n = ctx.Args.TryGetValue("n", out var b) ? int.Parse(b) : 6;
float gap = ctx.Args.TryGetValue("gap", out var c) ? float.Parse(c) : 3f;
float runup = ctx.Args.TryGetValue("runup", out var d) ? float.Parse(d) : 25f;
int ends = ctx.Args.TryGetValue("ends", out var e) ? int.Parse(e) : 1;
string prefix = ctx.Args.TryGetValue("prefix", out var f) ? f : "[Y]_[SM]_[B-";
DV.ThingTypes.TrainCarLivery L(string id) => DV.Globals.G.Types.Liveries.First(l => l.id == id);
var wagonLivery = L(ctx.Args.TryGetValue("wagon", out var w) ? w : "BoxcarBrown");
var locoLivery = L("LocoDE2");
const float WagonLength = 14.1f, LocoLength = 13f; // coupler to coupler, roughly
float pitch = WagonLength + gap;
float need = n * pitch + ends * (runup + LocoLength) + 20f;

var tracks = UnityEngine.Object.FindObjectsOfType<RailTrack>()
    .Where(t => t.name.StartsWith(prefix) && t.curve != null && t.curve.length >= need)
    .OrderBy(t => t.name).Take(k).ToList();
if (tracks.Count < k) throw new Exception($"only {tracks.Count} tracks named {prefix}* are at least {need:F0} m long");

TrainCar Spawn(RailTrack t, DV.ThingTypes.TrainCarLivery livery, double span)
{
    // SpawnCarFromRemote places a car at an exact point and is synced to clients (plain SpawnCar isn't).
    var p = t.GetKinkedPointSet().points.OrderBy(q => Math.Abs(q.span - span)).First();
    var car = CarSpawner.Instance.SpawnCarFromRemote(livery.prefab, t, (Vector3)p.position, (Vector3)p.forward);
    if (car == null) throw new Exception($"failed to spawn {livery.id} on {t.name} at {span:F0}");
    return car;
}

var rows = new List<object>(); var all = new List<string>(); var rams = new List<string>();
foreach (var t in tracks)
{
    CarSpawner.Instance.DeleteTrainCarsFromTrack(t);
    // Clean room: the yard's job generator keeps putting cars on and next to these tracks, and
    // clearing a track misses cars standing partly on the ladder. Rows spawned overlapping them blew
    // apart (job cars logged "derailed at 13385 km/h"), so derailments came from that, not the
    // crash. Delete anything within 3 m of the centreline (parallel tracks are 4.5 m away).
    var path = t.GetKinkedPointSet().points.Where((p, k) => k % 5 == 0).Select(p => (Vector3)p.position + WorldMover.currentMove).ToList();
    foreach (var other in UnityEngine.Object.FindObjectsOfType<TrainCar>().ToList())
        if (other != null && path.Any(p => Vector3.Distance(p, other.transform.position) < 3f))
            CarSpawner.Instance.DeleteCar(other);
    for (int i = 0; i < 10; i++) yield return null;
    double len = t.curve.length;
    double first = (len - n * pitch) / 2 + pitch / 2;           // middle of the first wagon
    var wagons = new List<TrainCar>();
    for (int i = 0; i < n; i++) wagons.Add(Spawn(t, wagonLivery, first + i * pitch));
    var locos = new List<(TrainCar loco, TrainCar target)>
    {
        (Spawn(t, locoLivery, first - pitch / 2 - runup - LocoLength / 2), wagons[0])
    };
    if (ends == 2)
        locos.Add((Spawn(t, locoLivery, first + (n - 1) * pitch + pitch / 2 + runup + LocoLength / 2), wagons[n - 1]));
    for (int i = 0; i < 30; i++) yield return null;
    foreach (var car in wagons.Concat(locos.Select(x => x.loco)))
    {
        foreach (var cp in new[] { car.frontCoupler, car.rearCoupler })
            if (cp.coupledTo != null) cp.Uncouple(false, false, false, false);
        car.brakeSystem.SetHandbrakePosition(1f);
        all.Add(car.ID);
    }
    foreach (var (loco, target) in locos) rams.Add(loco.ID + ":" + target.ID);
    rows.Add(new { track = t.name, wagons = string.Join(",", wagons.Select(x => x.ID)), locos = string.Join(",", locos.Select(x => x.loco.ID)) });
}
for (int i = 0; i < 60; i++) yield return new WaitForFixedUpdate();

// Nothing may already be moving or off the rails before the run starts.
var mine = all.Select(id => Trains.Find(id)).ToList();
var bad = mine.Where(c => c == null || c.derailed || c.rb.velocity.magnitude > 0.2f).Select(c => c == null ? "missing" : $"{c.ID} {(c.derailed ? "derailed" : $"{c.rb.velocity.magnitude:F1} m/s")}").ToList();
if (bad.Count > 0) throw new Exception("setup is not clean: " + string.Join(", ", bad));
ctx.Result = new { cars = string.Join(",", all), rams = string.Join(",", rams), rows };
