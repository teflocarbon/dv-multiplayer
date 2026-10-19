// Host: a coupled consist of loaded wagons on an empty track near the player, deleted twice in one
// frame: DV's slow bulk delete (DeleteTrainCarsFromTrack, what the yard's cleanup uses) and then
// DeleteCar on each car. Reports trainsets left holding a car that's gone (inactive, or without its
// NetworkedTrainCar), which used to refuse every join (#84).
var hopper = DV.Globals.G.Types.Liveries.First(x => x.id == "HopperBrown");
var player = PlayerManager.PlayerTransform.position;
var track = UnityEngine.Object.FindObjectsOfType<RailTrack>()
    .Where(t => t.curve != null && t.curve.length > 80 && !t.BogiesOnTrack().Any())
    .OrderBy(t => (t.transform.position - player).sqrMagnitude).First();
var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(Enumerable.Repeat(hopper, 4).ToList(), Enumerable.Repeat(false, 4).ToList(), track, true, true, 10.0, false);
for (int i = 0; i < 60; i++) yield return null;
foreach (var c in cars) c.logicCar.LoadCargo(c.logicCar.capacity, DV.ThingTypes.CargoType.Coal);
for (int i = 0; i < 60; i++) yield return null;
var spawned = new { track = track.name, cars = cars.Select(c => c.ID).ToList(), sets = cars.Select(c => c.trainset?.id ?? -1).Distinct().Count(), cargo = cars.Select(c => c.logicCar.LoadedCargoAmount).ToList() };
CarSpawner.Instance.DeleteTrainCarsFromTrack(track);
foreach (var c in cars) if (c != null && c.gameObject.activeInHierarchy) CarSpawner.Instance.DeleteCar(c);
yield return new WaitForSecondsRealtime(3f);
var stale = Trainset.allSets.Where(s => s.cars.Any(c => c == null || !c.gameObject.activeInHierarchy || c.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>() == null))
    .Select(s => s.id + ": " + string.Join(", ", s.cars.Select(c => c == null ? "null" : c.ID + (c.gameObject.activeInHierarchy ? "" : " inactive") + (c.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>() == null ? " unnetworked" : "")))).ToList();
ctx.Result = new { spawned, staleSets = stale };
