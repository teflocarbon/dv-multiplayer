// Host, diagnostic: why a loco spawned with its tender (Trains.SpawnWithTenderNearPlayer) ends up
// with no tender in its trainset. Spawns the loco of `livery` (default: the stoker loco pick.cs
// finds) the same way and reports, at 0, 2, 6 and 10 s: the tender chosen, each car's ID, livery,
// track and trainset, the distance between every pair of couplers of the two cars, which are coupled
// to what, preventAutoCouple, and the auto-coupler components the loco carries. Deletes both cars at
// the end. Run: dvmp eval -t host -m coroutine --timeout 60 -a livery=auto -f <this file>
string wanted = ctx.Args.TryGetValue("livery", out var w) && !string.IsNullOrEmpty(w) ? w : "auto";
if (wanted == "auto")
    wanted = DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.prefab != null &&
        l.prefab.GetComponentsInChildren<MonoBehaviour>(true).Any(m => m != null && m.GetType().Name.StartsWith("SteamMechanicalStokerDefinition")))?.id
        ?? throw new Exception("no stoker loco installed");
var livery = DV.Globals.G.Types.Liveries.First(l => l.id == wanted);
var tender = Trains.TenderOf(livery);
var autoCouplers = livery.prefab.GetComponentsInChildren<MonoBehaviour>(true)
    .Where(m => m != null && (m is DV.SteamTenderAutoCoupleMechanism || m.GetType().Name == "CarAutoCouplerInternal"))
    .Select(m => m.GetType().Name).Distinct().ToList();
var tenderCandidates = DV.Globals.G.Types.Liveries.Where(l => l.prefab != null && DV.ThingTypes.CarTypes.IsTender(l))
    .Select(l => l.id).ToList();
var before = new HashSet<TrainCar>(UnityEngine.Object.FindObjectsOfType<TrainCar>());
var loco = Trains.SpawnWithTenderNearPlayer(wanted);
var spawned = UnityEngine.Object.FindObjectsOfType<TrainCar>().Where(c => !before.Contains(c)).ToList();
var cars = loco.trainset.cars.ToList();
TrainCar other = spawned.FirstOrDefault(c => c != loco);
var samples = new List<object>();
float t0 = Time.realtimeSinceStartup;
foreach (float at in new[] { 0f, 2f, 6f, 10f })
{
    while (Time.realtimeSinceStartup - t0 < at)
        yield return null;
    object Car(TrainCar c) => c == null ? null : new
    {
        c.ID, livery = c.carLivery.id, track = c.Bogies[0].track?.name, trainset = c.trainset?.id, setCars = c.trainset?.cars.Count,
        front = new { coupled = c.frontCoupler.coupledTo?.train?.ID, c.frontCoupler.preventAutoCouple },
        rear = new { coupled = c.rearCoupler.coupledTo?.train?.ID, c.rearCoupler.preventAutoCouple }
    };
    var gaps = other == null ? null : new Dictionary<string, double>
    {
        ["locoRear-tenderFront"] = Math.Round(Vector3.Distance(loco.rearCoupler.transform.position, other.frontCoupler.transform.position), 2),
        ["locoRear-tenderRear"] = Math.Round(Vector3.Distance(loco.rearCoupler.transform.position, other.rearCoupler.transform.position), 2),
        ["locoFront-tenderFront"] = Math.Round(Vector3.Distance(loco.frontCoupler.transform.position, other.frontCoupler.transform.position), 2),
        ["locoFront-tenderRear"] = Math.Round(Vector3.Distance(loco.frontCoupler.transform.position, other.rearCoupler.transform.position), 2),
    };
    samples.Add(new { t = at, loco = Car(loco), tender = Car(other), gaps });
}
ctx.Result = new { livery = wanted, tender = tender?.id, autoCouplers, tenderCandidates, spawned = spawned.Select(c => c.ID + " " + c.carLivery.id).ToList(), spawnedSetCars = cars.Count, samples };
// Only the cars this spawned.
foreach (var c in spawned)
    Trains.Delete(c);
