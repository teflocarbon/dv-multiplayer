// Host: the loco's values for the given ports (comma separated), and how many control leases are
// still held (none should be, once every interaction is over).
var car = Trains.Find(ctx.Args["loco"]);
var ntc = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>();
var values = ctx.Args["ports"].Split(',').ToDictionary(p => p, p => ntc.simulationFlow.TryGetPort(p, out var port) ? Math.Round(port.Value, 3) : -1);
return new { values, leases = Multiplayer.Server.Trains.NetworkedTrainCarServer.Of(ntc).LeaseCount };
