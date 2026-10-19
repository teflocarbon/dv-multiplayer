// Host: spawns the loco near the host's player where it stays put: a steam loco with its tender
// coupled, every car's handbrake on, and a spot where it doesn't roll. The S282's handbrake is on
// its tender: spawned alone, its handbrake set did nothing, it rolled away on a grade and left a
// boarded client behind, out of reach of its controls (#159). With the tender's handbrake on, an
// S282A and tender still crept 15-18 m in a minute on some grades (batch 5b): each spot is watched
// for a few seconds, and one the loco moves on is given up for one further out.
// Returns the loco's id, its tender's, where it is (absolute) and the spots it gave up.
var tried = new List<string>();
foreach (float ahead in new[] { 25f, 45f, 70f, 100f, 140f })
{
    var car = Trains.SpawnWithTenderNearPlayer(ctx.Args["t"], ahead);
    var tenderLivery = Trains.TenderOf(car.carLivery);
    TrainCar tender = tenderLivery == null ? null : UnityEngine.Object.FindObjectsOfType<TrainCar>()
        .Where(c => c.carLivery == tenderLivery).OrderBy(c => (c.transform.position - car.transform.position).sqrMagnitude).FirstOrDefault();
    foreach (var c in new[] { car, tender }.Where(c => c != null))
        c.brakeSystem.SetHandbrakePosition(1f);
    yield return Trains.CoupleTender(car, 1.5f);
    for (int i = 0; i < 120; i++) yield return null;
    Vector3 from = Dev.Abs(car.transform.position);
    yield return new WaitForSecondsRealtime(5f);
    float moved = Vector3.Distance(Dev.Abs(car.transform.position), from);
    if (moved < 0.05f)
    {
        Vector3 at = Dev.Abs(car.transform.position);
        ctx.Result = new { id = car.ID, tender = tender?.ID ?? "", x = at.x, y = at.y, z = at.z, tried = string.Join("; ", tried) };
        yield break;
    }
    tried.Add($"{ahead} m ahead: moved {moved:F2} m in 5 s");
    foreach (var c in new[] { car, tender }.Where(c => c != null))
        Trains.Delete(c);
    for (int i = 0; i < 30; i++) yield return null;
}
throw new Exception(ctx.Args["t"] + " rolled on every spot tried: " + string.Join("; ", tried));
