// Either side: the coupling between the loco and the wagon as this game has it, whether a player
// may use the chains as vanilla lets them, and on the host whether the server half of either car
// still has that end marked as being interacted with.
// Vanilla only gives the chain on the hook a screw button, and turns knobs off in Attached_Tight
// and in every Other_Attached state (the other car's chain while one is on the hook): screw and
// knob are checked against that, not against "everything takes input" (the 2026-10-20 run read
// the parked chain's knob, off as vanilla has it, as the bug). The bug this is after is the hooked
// chain's screw button left not taking input (the Start path's old fallback on the host).
// Chains only switch on when a camera renders them, and the test games are minimised: they're
// locked on while this reads them, as screw.cs does.
var a = Trains.Find(ctx.Args["loco"]); var b = Trains.Find(ctx.Args["wagon"]);
Coupler ca = bool.Parse(ctx.Args["locoFront"]) ? a.frontCoupler : a.rearCoupler;
Coupler cb = bool.Parse(ctx.Args["wagonFront"]) ? b.frontCoupler : b.rearCoupler;
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
var optimizers = new[] { a, b }.SelectMany(c => c.GetComponentsInChildren<ChainCouplerVisibilityOptimizer>(true)).ToList();
foreach (var o in optimizers) o.LockLOD();
bool Enabled(Coupler c) => c.ChainScript != null && c.ChainScript.CurrentState != ChainCouplerInteraction.State.Disabled;
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 10f && !(Enabled(ca) && Enabled(cb));) yield return null;
// Settled into their states.
for (int i = 0; i < 30; i++) yield return null;

DV.CabControls.ButtonBase Screw(ChainCouplerInteraction chain) =>
    chain.screwButton != null && chain.screwButton.activeInHierarchy ? chain.screwButton.GetComponent<DV.CabControls.ButtonBase>() : null;
DV.CabControls.GizmoBase Knob(ChainCouplerInteraction chain) =>
    typeof(ChainCouplerInteraction).GetField("knobGizmo", flags).GetValue(chain) as DV.CabControls.GizmoBase;
bool Hooked(ChainCouplerInteraction chain) =>
    chain.CurrentState is ChainCouplerInteraction.State.Attached_Tight or ChainCouplerInteraction.State.Attached_Loose;
bool KnobExpected(ChainCouplerInteraction chain) =>
    !(chain.CurrentState is ChainCouplerInteraction.State.Attached_Tight or ChainCouplerInteraction.State.Other_Attached
      or ChainCouplerInteraction.State.Other_Attached_Parked or ChainCouplerInteraction.State.Other_Attached_Dangling);
// S/s: a screw button, taking input or not; K/k: the knob, taking input or not; -: none.
string Allowed(Coupler c)
{
    var chain = c.ChainScript;
    if (!Enabled(c)) return "--";
    var screw = Screw(chain); var knob = Knob(chain);
    return (screw == null ? "-" : screw.InteractionAllowed ? "S" : "s") + (knob == null ? "-" : knob.InteractionAllowed ? "K" : "k");
}
var problems = new List<string>();
foreach (var (c, who) in new[] { (ca, "loco's"), (cb, "wagon's") })
{
    if (!Enabled(c)) { problems.Add($"the {who} chain never switched on"); continue; }
    var chain = c.ChainScript;
    var screw = Screw(chain); var knob = Knob(chain);
    if (Hooked(chain) && (screw == null || !screw.InteractionAllowed))
        problems.Add($"the {who} chain is on the hook ({chain.CurrentState}) and its screw button doesn't take input");
    if (knob != null && knob.InteractionAllowed != KnobExpected(chain))
        problems.Add($"the {who} chain's knob {(knob.InteractionAllowed ? "takes" : "doesn't take")} input in {chain.CurrentState}");
}
string InUse(Coupler c)
{
    var server = Multiplayer.Server.Trains.NetworkedTrainCarServer.Of(c.train.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>());
    if (server == null) return "-";
    var field = typeof(Multiplayer.Server.Trains.NetworkedTrainCarServer).GetField(c.isFrontCoupler ? "frontInteracting" : "rearInteracting", flags);
    return (bool)field.GetValue(server) ? "in use" : "free";
}
var result = new
{
    coupled = ca.coupledTo == cb,
    sameSet = a.trainset == b.trainset,
    chain = ca.state + "/" + cb.state,
    states = (ca.ChainScript?.CurrentState.ToString() ?? "none") + "/" + (cb.ChainScript?.CurrentState.ToString() ?? "none"),
    tight = ca.state == ChainCouplerInteraction.State.Attached_Tight || cb.state == ChainCouplerInteraction.State.Attached_Tight,
    allowed = Allowed(ca) + "/" + Allowed(cb),
    problems = string.Join("; ", problems),
    inUse = InUse(ca) + "/" + InUse(cb)
};
foreach (var o in optimizers) o.UnlockLOD();
ctx.Result = result;
