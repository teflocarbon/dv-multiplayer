// Either side: the coupling between the loco and the wagon as this game has it, whether a player
// may use both chains (their screw buttons and knobs), and on the host whether the server half of
// either car still has that end marked as being interacted with.
var a = Trains.Find(ctx.Args["loco"]); var b = Trains.Find(ctx.Args["wagon"]);
Coupler ca = bool.Parse(ctx.Args["locoFront"]) ? a.frontCoupler : a.rearCoupler;
Coupler cb = bool.Parse(ctx.Args["wagonFront"]) ? b.frontCoupler : b.rearCoupler;
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
string Allowed(Coupler c)
{
    var chain = c.ChainScript;
    if (chain == null) return "-";
    var screw = chain.screwButton != null ? chain.screwButton.GetComponent<DV.CabControls.ButtonBase>() : null;
    var knob = typeof(ChainCouplerInteraction).GetField("knobGizmo", flags).GetValue(chain) as DV.CabControls.GizmoBase;
    return (screw == null ? "-" : screw.InteractionAllowed ? "S" : "s") + (knob == null ? "-" : knob.InteractionAllowed ? "K" : "k");
}
string InUse(Coupler c)
{
    var server = Multiplayer.Server.Trains.NetworkedTrainCarServer.Of(c.train.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>());
    if (server == null) return "-";
    var field = typeof(Multiplayer.Server.Trains.NetworkedTrainCarServer).GetField(c.isFrontCoupler ? "frontInteracting" : "rearInteracting", flags);
    return (bool)field.GetValue(server) ? "in use" : "free";
}
return new
{
    coupled = ca.coupledTo == cb,
    sameSet = a.trainset == b.trainset,
    chain = ca.state + "/" + cb.state,
    tight = ca.state == ChainCouplerInteraction.State.Attached_Tight || cb.state == ChainCouplerInteraction.State.Attached_Tight,
    allowed = Allowed(ca) + "/" + Allowed(cb),
    inUse = InUse(ca) + "/" + InUse(cb)
};
