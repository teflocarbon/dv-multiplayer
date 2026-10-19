// Host: spawns n boxcars as one consist on a track with no other cars on it, waits for the client
// to have them, then deletes them and spawns n new boxcars on the same track in the same frame,
// so DV's car pool hands the deleted objects to the new cars. Cars must never be spawned inside
// each other: overlapping cars couple into loops that DV's trainset code can't split when they
// break apart, which leaves an empty trainset behind. Args: n.
int n = int.Parse(ctx.Args["n"]);
var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First();
var livery = DV.Globals.G.Types.Liveries.First(l => l.id == "BoxcarBrown");
var liveries = Enumerable.Repeat(livery, n).ToList();
var flips = liveries.Select(_ => false).ToList();
ushort NetId(TrainCar c) => c.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>()?.NetId ?? 0;

RailTrack track = null;
foreach (float d in new[] { 30f, 60f, 90f, 120f, 160f, 200f })
foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
{
    if (track != null) break;
    var t = CarSpawner.GetTrackClosestTo(client.transform.position + dir * d, 60f, out int _);
    if (t == null || t.GetKinkedPointSet().span < 80f) continue;
    if (CarSpawner.Instance.AllCars.Any(c => c != null && c.Bogies.Any(b => b.track == t))) continue;
    track = t;
}
if (track == null) throw new Exception("no empty track near the client");

var old = CarSpawner.Instance.SpawnCarTypesOnTrack(liveries, flips, track, true, false);
if (old == null) throw new Exception("couldn't spawn on " + track.name);
for (int i = 0; i < 120; i++) yield return null;
var oldIds = old.Select(NetId).ToList();
var oldObjects = new HashSet<TrainCar>(old);
foreach (var c in old) CarSpawner.Instance.DeleteCar(c);
var fresh = CarSpawner.Instance.SpawnCarTypesOnTrack(liveries, flips, track, true, false);
if (fresh == null) throw new Exception("couldn't respawn on " + track.name);
for (int i = 0; i < 120; i++) yield return null;
ctx.Result = new
{
    track = track.name,
    cars = string.Join(",", fresh.Select(c => c.ID)),
    reused = fresh.Count(c => oldObjects.Contains(c)),
    oldIds = string.Join(",", oldIds),
    ids = string.Join(",", fresh.Select(c => c.ID + ":" + NetId(c))),
    emptyTrainsets = Trainset.allSets.Count(s => s.cars.Count == 0)
};
