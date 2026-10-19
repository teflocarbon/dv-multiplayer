// Client: every cab control that feeds a sim port must be hooked, or the client never takes its
// lease and drops every change to it (#73). A port another control already drives (a steam loco's
// whistle rope owns the whistle's dummy lever) counts as hooked, and so does every port of a control
// that feeds several.
var car = Trains.Find(ctx.Args["loco"]);
var ntc = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>();
if (car.loadedInterior == null) return new { feeders = 0, unhooked = new List<string> { "no interior loaded" } };
var feeders = car.loadedInterior.GetComponentsInChildren<DV.Simulation.Ports.InteractablePortFeeder>(true);
var unhooked = feeders.Where(f => !ntc.portNetIdToControl.ContainsKey(Multiplayer.Shared.Trains.NetworkedTrainCar.GetPortNetId(f.portId)))
    .Select(f => f.name + " (" + f.portId + ")").ToList();
return new { feeders = feeders.Length, unhooked };
