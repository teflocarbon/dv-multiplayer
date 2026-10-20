// Host: the host's player arrives in a loco's cab and takes a lever in the same frame, as a player
// who jumps aboard and grabs at once. The server must range-check the host where it is now (its
// position is read from its tracker right before its requests are handled), not where the last
// position it had put it, 60 m away. Then lets go. Args: loco, control.
// Returns where the server had the host before (far: its distance from the loco), the lease holder
// after the grab ("host", "none" or a name) and the lease count after letting go.
var car = Trains.Find(ctx.Args["loco"]);
var ntc = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>();
var server = Multiplayer.Shared.Session.NetworkLifecycle.Instance.Server;
var client = Multiplayer.Shared.Session.NetworkLifecycle.Instance.Client;

yield return Trains.Board(car);
var c = car.loadedInterior.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true).First(x => x.name == ctx.Args["control"]);
uint netId = Multiplayer.Shared.Trains.NetworkedTrainCar.GetPortNetId(c.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>().portId);
Vector3 floor = Dev.Abs(Trains.FloorPoint(car)) + Vector3.up * 0.1f;

// Away from the loco, long enough for the server to have the host there.
Vector3 away = Dev.Abs(car.transform.position + car.transform.right * 60f);
yield return LocalPlayer.TeleportToGround(away.x, away.z, 2f);
float farBefore = Vector3.Distance(server.LocalPlayer.WorldPosition, car.transform.position);

// Back in the cab and grab, in one frame.
PlayerManager.TeleportPlayer(Dev.World(floor), PlayerManager.PlayerTransform.rotation, null, false);
float movedAtOnce = Vector3.Distance(Dev.Abs(PlayerManager.PlayerTransform.position), floor);
client.SendTrainControlAuthorityRequest(ntc.NetId, netId, true);
yield return new WaitForSecondsRealtime(1f);

var serverPart = Multiplayer.Server.Trains.NetworkedTrainCarServer.Of(ntc);
var owner = serverPart.LeaseHolder(netId);
string holder = owner == null ? "none" : owner == server.LocalPlayer ? "host" : owner.Username;
float serverDistance = Vector3.Distance(server.LocalPlayer.WorldPosition, PlayerManager.PlayerTransform.position);

client.SendTrainControlAuthorityRequest(ntc.NetId, netId, false);
yield return new WaitForSecondsRealtime(1f);

ctx.Result = new
{
    farBefore = Math.Round(farBefore, 1),
    movedAtOnce = Math.Round(movedAtOnce, 2),
    holder,
    serverDistance = Math.Round(serverDistance, 2),
    leasesAfter = serverPart.LeaseCount
};
