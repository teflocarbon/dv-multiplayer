// Client: sets the cab controls feeding these ports straight to a value, with no grab and no Use():
// all the mod sees of a VR hand turning a valve, or of a click on a cab window. Returns each port
// and the client's value once it has settled.
var car = Trains.Find(ctx.Args["loco"]);
var ports = ctx.Args["ports"].Split(',');
float value = float.Parse(ctx.Args["value"], System.Globalization.CultureInfo.InvariantCulture);
// One control per port: the cab's if it has one, else the car body's (the DE6's windows, the S282's
// oiling points: the external interactables, which are under the car's interior transform, not
// under the car), never their dummy copy. Setting a second control of a port the first already
// moved is no change, so nothing would be sent for it, and the port could end up moved by the one
// the player doesn't use.
var roots = new[] { car.loadedInterior, car.loadedExternalInteractables, car.gameObject }.Where(root => root != null).ToList();
var controls = roots
    .SelectMany(root => root.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true))
    .Where(c => car.loadedDummyExternalInteractables == null || !c.transform.IsChildOf(car.loadedDummyExternalInteractables.transform))
    .Select(c => new { control = c, port = c.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>()?.portId })
    .Where(x => x.port != null && ports.Contains(x.port)).GroupBy(x => x.port).Select(g => g.First()).ToList();
if (controls.Count == 0) throw new Exception("no cab control feeds " + ctx.Args["ports"]);
foreach (var x in controls)
{
    x.control.SetValue(value);
    yield return new WaitForSecondsRealtime(0.2f);
}
yield return new WaitForSecondsRealtime(2.5f);
var flow = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>().simulationFlow;
ctx.Result = controls.Select(x => x.port).Distinct().ToDictionary(id => id, id => flow.TryGetPort(id, out var p) ? Math.Round(p.Value, 3) : -1);
