// Spawns a tank car (handbrake on) on the track nearest the client's player, not the host's own,
// then waits up to 120 s for the host to have ground under it (the server loads every Near tile in
// the background after a load). Coroutine; result: car ID, the player, whether there's ground, how
// long it waited.
var lifecycle = NetworkLifecycle.Instance;
var player = Multiplayer.Server.Session.NetworkServer.Instance.ServerPlayers.First(p => p != Multiplayer.Server.Session.NetworkServer.Instance.LocalPlayer);
var livery = DV.Globals.G.Types.Liveries.First(l => l.id == "TankChrome");
var car = CarSpawner.Instance.SpawnCarOnClosestTrack(player.WorldPosition, livery, false, true, false) ?? throw new Exception("no room to spawn next to the client's player");
car.brakeSystem.SetHandbrakePosition(1f);
for (int i = 0; i < 180; i++) yield return null;
float start = Time.realtimeSinceStartup;
bool ground = false;
while (Time.realtimeSinceStartup - start < 120f)
{
    ground = ServerWorldCoverage.Instance != null ? ServerWorldCoverage.MayMove(car) :
        UnityEngine.Object.FindObjectOfType<WorldStreamingInit>().IsSceneAndTerrainCellLoaded(car.transform.position);
    if (ground) break;
    yield return new WaitForSecondsRealtime(1f);
}
ctx.Result = new
{
    id = car.ID,
    player = player.Username,
    ground,
    waited = Math.Round(Time.realtimeSinceStartup - start),
    coverage = ServerWorldCoverage.Instance != null,
    dedicated = lifecycle.IsDedicatedHost,
};
