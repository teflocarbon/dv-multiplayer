// Host: a car (LIVERY) on the track nearest the client's player, handbrake on, and where to rerail
// it: `shift` metres along that track from where it stands (or the other way, or where it stands,
// whichever is on the track). Coroutine; result: car ID, the track's net ID, the rerail point
// (absolute) and forward.
var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First();
var livery = DV.Globals.G.Types.Liveries.First(l => l.id == ctx.Args["livery"]);
float shift = float.Parse(ctx.Args["shift"], System.Globalization.CultureInfo.InvariantCulture);
TrainCar car = null;
foreach (float d in new[] { 20f, 40f, 60f, 80f })
foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
{
    if (car == null)
        car = CarSpawner.Instance.SpawnCarOnClosestTrack(client.transform.position + dir * d, livery, false, true, false);
}
if (car == null) throw new Exception("no room to spawn " + livery.id);
car.brakeSystem.SetHandbrakePosition(1f);
for (int i = 0; i < 120; i++) yield return null;
var track = car.Bogies[0].track;
Vector3 pos = car.transform.position, fwd = car.transform.forward, target = pos;
foreach (float s in new[] { shift, -shift, 0f })
{
    Vector3 p = pos + fwd * s;
    var (point, _) = RailTrack.GetClosestPoint(track, p);
    if (point.HasValue && Vector3.Distance((Vector3)point.Value.position + WorldMover.currentMove, p) < 1.5f) { target = p; shift = s; break; }
}
var at = Dev.Abs(target);
ctx.Result = new
{
    id = car.ID,
    track = Multiplayer.Shared.World.NetworkedRailTrack.GetFromRailTrack(track).NetId,
    x = at.x, y = at.y, z = at.z, fx = fwd.x, fy = fwd.y, fz = fwd.z,
    shift,
};
