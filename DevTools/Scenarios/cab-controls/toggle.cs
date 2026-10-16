// Client: flips every toggle switch in the cab with Use(), which is all a VR hand does (it never
// raises Grabbed), then returns each switch's port and the client's value. Run twice to flip back.
var car = Trains.Find(ctx.Args["loco"]);
// Every port each switch feeds (one switch can feed several).
var switches = car.loadedInterior.GetComponentsInChildren<DV.CabControls.ToggleSwitchBase>(true)
    .Select(s => new { control = s, ports = s.GetComponents<DV.Simulation.Ports.InteractablePortFeeder>().Select(f => f.portId).Where(id => !string.IsNullOrEmpty(id)).ToList() })
    .Where(s => s.ports.Count > 0).ToList();
foreach (var s in switches)
{
    s.control.Use();
    yield return new WaitForSecondsRealtime(0.1f);
}
yield return new WaitForSecondsRealtime(2f);
var flow = car.GetComponent<Multiplayer.Components.Networking.Train.NetworkedTrainCar>().simulationFlow;
ctx.Result = switches.SelectMany(s => s.ports).Distinct().ToDictionary(id => id, id => flow.TryGetPort(id, out var p) ? Math.Round(p.Value, 3) : -1);
