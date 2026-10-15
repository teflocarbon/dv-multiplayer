// Client: couple the loco and wagon here only, the way a bounce can leave a client coupled where
// the host isn't. Inside the authoritative-topology scope, so nothing is sent to the host (the
// same path a host snapshot takes). By the chain, without hoses: a coupling with hoses splits the
// brake line, which the host's brake state updates notice within a couple of seconds and resync,
// but a chain-only coupling leaves the brake lines alone and nothing else catches it.
// Args: loco, wagon, locoFront, wagonFront.
var a = Trains.Find(ctx.Args["loco"]); var b = Trains.Find(ctx.Args["wagon"]);
Coupler ca = bool.Parse(ctx.Args["locoFront"]) ? a.frontCoupler : a.rearCoupler;
Coupler cb = bool.Parse(ctx.Args["wagonFront"]) ? b.frontCoupler : b.rearCoupler;
using (Multiplayer.Components.Networking.Train.NetworkedTrainCar.BeginAuthoritativeTopologyApplication())
    ca.CoupleTo(cb, false, true);
for (int i = 0; i < 30; i++) yield return new WaitForFixedUpdate();
ctx.Result = new { coupled = ca.coupledTo == cb, sameSet = a.trainset == b.trainset, joint = ca.rigidCJ != null || cb.rigidCJ != null, hose = ca.GetAirHoseConnectedTo() == cb };
