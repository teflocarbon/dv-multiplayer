// Client: sets ONE copy of each port's control, the cab's (which=cab) or the outside one
// (which=external, the car's external interactables, not their dummy), straight to a value with no
// grab. Returns how many copies each port has where, and the client's port values once settled.
var car = Trains.Find(ctx.Args["loco"]);
string which = ctx.Args["which"];
var ports = ctx.Args["ports"].Split(',');
float value = float.Parse(ctx.Args["value"], System.Globalization.CultureInfo.InvariantCulture);
var dummy = car.loadedDummyExternalInteractables;
System.Func<GameObject, string, IEnumerable<(DV.CabControls.ControlImplBase control, string where, string port)>> under = (root, where) =>
    root == null ? Enumerable.Empty<(DV.CabControls.ControlImplBase, string, string)>()
    : root.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true)
        .Select(c => (c, dummy != null && c.transform.IsChildOf(dummy.transform) ? "dummy" : where,
                      c.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>()?.portId))
        .Where(x => x.Item3 != null && ports.Contains(x.Item3));
var all = under(car.loadedInterior, "cab").Concat(under(car.loadedExternalInteractables, "external"))
    .Concat(under(dummy, "dummy")).ToList();
var copies = ports.ToDictionary(p => p, p => string.Join(",", all.Where(x => x.port == p).Select(x => x.where)));
var chosen = ports.Select(p => all.FirstOrDefault(x => x.port == p && x.where == which)).Where(x => x.control != null).ToList();
if (chosen.Count == 0) throw new Exception("no " + which + " control feeds " + ctx.Args["ports"] + "; copies: " + string.Join("; ", copies.Select(kv => kv.Key + "=" + kv.Value)));
foreach (var x in chosen)
{
    x.control.SetValue(value);
    yield return new WaitForSecondsRealtime(0.2f);
}
yield return new WaitForSecondsRealtime(2.5f);
var flow = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>().simulationFlow;
ctx.Result = new
{
    set = chosen.Select(x => x.port + "@" + x.where + ":" + x.control.name).ToArray(),
    copies,
    values = chosen.Select(x => x.port).Distinct().ToDictionary(id => id, id => flow.TryGetPort(id, out var p) ? Math.Round(p.Value, 3) : -1),
};
