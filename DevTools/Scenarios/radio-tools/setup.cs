// Host: spawns a car of ctx.Args["livery"] near the host's player. Returns its ID and NetId, and
// the track and kinked-point index it stands on (where a work train can be summoned once it's gone).
var car = Trains.SpawnNearPlayer(ctx.Args["livery"]);
for (int i = 0; i < 120; i++) yield return null;
Multiplayer.Shared.Trains.NetworkedTrainCar.TryGetFromTrainCar(car, out var n);
var track = car.Bogies[0].track;
var points = track.GetKinkedPointSet().points;
Vector3 abs = car.transform.position - WorldMover.currentMove;
int index = Enumerable.Range(0, points.Length).OrderBy(i => ((Vector3)points[i].position - abs).sqrMagnitude).First();
ctx.Result = new
{
    id = car.ID,
    netId = n.NetId,
    track = Multiplayer.Shared.World.NetworkedRailTrack.GetFromRailTrack(track).NetId,
    index
};
