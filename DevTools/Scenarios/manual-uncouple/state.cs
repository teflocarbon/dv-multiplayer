// The coupling between the loco and wagon, as this peer sees it.
var a = Trains.Find(ctx.Args["loco"]); var b = Trains.Find(ctx.Args["wagon"]);
Coupler ca = bool.Parse(ctx.Args["locoFront"]) ? a.frontCoupler : a.rearCoupler;
Coupler cb = bool.Parse(ctx.Args["wagonFront"]) ? b.frontCoupler : b.rearCoupler;
return new
{
    coupled = ca.coupledTo == cb,
    sameSet = a.trainset == b.trainset,
    hose = ca.GetAirHoseConnectedTo() == cb,
    cocks = (ca.IsCockOpen ? "O" : "C") + (cb.IsCockOpen ? "O" : "C"),
    chain = ca.state + "/" + cb.state,
    fsm = (ca.ChainScript?.CurrentState.ToString() ?? "-") + "/" + (cb.ChainScript?.CurrentState.ToString() ?? "-"),
    gap = Math.Round(Vector3.Distance(ca.transform.position, cb.transform.position), 2),
    wagonAbs = Dev.Abs(b.transform.position)
};
