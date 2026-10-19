// Client: grabs a cab control (the game's own Grabbed event, as a hand or the mouse raises it),
// turns it to a value, lets go. Returns the port and the client's value once it has settled.
var car = Trains.Find(ctx.Args["loco"]);
var c = car.loadedInterior.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true).First(x => x.name == ctx.Args["control"]);
c.FireGrabbed();
yield return new WaitForSecondsRealtime(0.3f);
c.SetValue(float.Parse(ctx.Args["value"], System.Globalization.CultureInfo.InvariantCulture));
yield return new WaitForSecondsRealtime(0.5f);
c.FireUngrabbed();
yield return new WaitForSecondsRealtime(2.5f);
var port = c.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>().portId;
var flow = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>().simulationFlow;
ctx.Result = new Dictionary<string, double> { [port] = flow.TryGetPort(port, out var p) ? Math.Round(p.Value, 3) : -1 };
