// Host: spawns the loco near the host's player, a steam loco with its tender coupled, every car's
// handbrake on. The S282's handbrake is on its tender: spawned alone, its handbrake set did nothing,
// it rolled away on a grade and left a boarded client behind, out of reach of its controls (#159).
// Returns the loco's id and where it is (absolute), to check it stayed there.
var car = Trains.SpawnWithTenderNearPlayer(ctx.Args["t"]);
var tenderLivery = Trains.TenderOf(car.carLivery);
TrainCar tender = tenderLivery == null ? null : UnityEngine.Object.FindObjectsOfType<TrainCar>()
    .Where(c => c.carLivery == tenderLivery).OrderBy(c => (c.transform.position - car.transform.position).sqrMagnitude).FirstOrDefault();
foreach (var c in new[] { car, tender }.Where(c => c != null))
    c.brakeSystem.SetHandbrakePosition(1f);
yield return Trains.CoupleTender(car, 1.5f);
for (int i = 0; i < 120; i++) yield return null;
Vector3 at = Dev.Abs(car.transform.position);
ctx.Result = new { id = car.ID, tender = tender?.ID ?? "", x = at.x, y = at.y, z = at.z };
