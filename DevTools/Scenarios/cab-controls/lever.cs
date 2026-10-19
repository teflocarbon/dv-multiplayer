// Either side: grabs a cab control as a hand or the mouse does (the game's Grabbed event) and,
// given a value, turns it there and keeps holding; or lets go of it (action=release). Returns the
// control's port, this game's value for it, and whether this game holds its lease and sees it
// blocked by someone else's.
var car = Trains.Find(ctx.Args["loco"]);
var ntc = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>();
var c = car.loadedInterior.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true).First(x => x.name == ctx.Args["control"]);
var port = c.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>().portId;
uint netId = Multiplayer.Shared.Trains.NetworkedTrainCar.GetPortNetId(port);
if (ctx.Args["action"] == "release")
{
    c.FireUngrabbed();
    // A sprung control is let go once it has settled (polled every 0.5 s), then the lease goes.
    yield return new WaitForSecondsRealtime(3f);
}
else
{
    c.FireGrabbed();
    yield return new WaitForSecondsRealtime(0.5f);
    if (ctx.Args.TryGetValue("value", out var v) && !string.IsNullOrEmpty(v))
    {
        c.SetValue(float.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
        yield return new WaitForSecondsRealtime(0.5f);
    }
    yield return new WaitForSecondsRealtime(1.5f);
}
ctx.Result = new
{
    port,
    value = ntc.simulationFlow.TryGetPort(port, out var p) ? Math.Round(p.Value, 3) : -1,
    owned = ntc.locallyOwnedPortAuthorities.Contains(netId),
    blocked = ntc.portNetIdToBlockState.TryGetValue(netId, out bool b) && b
};
