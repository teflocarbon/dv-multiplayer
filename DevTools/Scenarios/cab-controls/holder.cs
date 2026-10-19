// Host: who holds the lease on a cab control's port ("none", "host" for the host's own player,
// else the player's name), the host's value for it, whether the host's cab sees it blocked, and how
// many leases the loco has.
var car = Trains.Find(ctx.Args["loco"]);
var ntc = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>();
var c = car.loadedInterior.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true).First(x => x.name == ctx.Args["control"]);
var port = c.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>().portId;
uint netId = Multiplayer.Shared.Trains.NetworkedTrainCar.GetPortNetId(port);
var local = Multiplayer.Shared.Session.NetworkLifecycle.Instance.Server.LocalPlayer;
string holder = !ntc.portAuthority.TryGetValue(netId, out var owner) || owner == null ? "none"
    : owner == local ? "host" : owner.Username;
return new
{
    holder,
    value = ntc.simulationFlow.TryGetPort(port, out var p) ? Math.Round(p.Value, 3) : -1,
    blocked = ntc.portNetIdToBlockState.TryGetValue(netId, out bool b) && b,
    leases = ntc.portAuthority.Count
};
