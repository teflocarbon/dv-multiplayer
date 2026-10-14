// Either peer: wait for the couplers to come into range and couple then, as a player using the
// couple key / coupling menu at the moment of contact, or (chain=1) grabbing the wagon's chain and
// dropping it on the loco's hook. Args: loco, wagon, locoFront, wagonFront, chain.
var loco = Trains.Find(ctx.Args["loco"]); var wagon = Trains.Find(ctx.Args["wagon"]);
Coupler ca = bool.Parse(ctx.Args["locoFront"]) ? loco.frontCoupler : loco.rearCoupler;
Coupler cb = bool.Parse(ctx.Args["wagonFront"]) ? wagon.frontCoupler : wagon.rearCoupler;
bool viaChain = ctx.Args.TryGetValue("chain", out var ch) && ch == "1";
// Stand beside the wagon's coupler, as a player waiting to couple would (the host checks it).
if (!NetworkLifecycle.Instance.IsHost())
    yield return LocalPlayer.TeleportToGround(Dev.Abs(cb.transform.position).x + 2.5f, Dev.Abs(cb.transform.position).z);
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
var optimizers = new[] { loco, wagon }.SelectMany(c => c.GetComponentsInChildren<ChainCouplerVisibilityOptimizer>(true)).ToList();
if (viaChain) foreach (var o in optimizers) o.LockLOD();
var helper = new DV.HUD.UICouplingHelper { trainCar = wagon, shouldAutoHandbrake = () => false };
float gapAt = -1;
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 30f;)
{
    yield return new WaitForFixedUpdate();
    float gap = Vector3.Distance(ca.transform.position, cb.transform.position);
    if (gap >= (viaChain ? 1.6f : 0.9f)) continue;
    gapAt = gap;
    if (!viaChain) { helper.HandleCoupling(cb, false); break; }
    // Grab the chain as the cars close and hold it on the hook through the bounce.
    var chain = cb.ChainScript; var other = ca.ChainScript;
    var knob = (GameObject)typeof(ChainCouplerInteraction).GetField("knob", flags).GetValue(chain);
    typeof(ChainCouplerInteraction).GetMethod("OnGrabbed", flags).Invoke(chain, new object[] { null });
    for (int i = 0; i < 40; i++) { knob.transform.position = other.ownAttachPoint.transform.position; yield return null; }
    typeof(ChainCouplerInteraction).GetMethod("OnUngrabbed", flags).Invoke(chain, new object[] { null });
    break;
}
// hitch=ms: stall a few frames during the bounce, as a stutter would
int hitch = ctx.Args.TryGetValue("hitch", out var hs) ? int.Parse(hs) : 0;
for (int i = 0; i < 4 && hitch > 0; i++) { System.Threading.Thread.Sleep(hitch); yield return null; }
for (int i = 0; i < 30; i++) yield return new WaitForFixedUpdate();
if (viaChain) foreach (var o in optimizers) o.UnlockLOD();
ctx.Result = new { how = viaChain ? "chain" : "menu", gapAt = Math.Round(gapAt, 2), coupledNow = ca.coupledTo == cb, fsm = cb.ChainScript?.CurrentState.ToString() ?? "-" };
