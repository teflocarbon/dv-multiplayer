// Client: couple by hand, a step a time: lift a parked chain onto the other car's hook, tighten
// the screw, join the hoses and open both angle cocks.
var a = Trains.Find(ctx.Args["loco"]); var b = Trains.Find(ctx.Args["wagon"]);
Coupler ca = bool.Parse(ctx.Args["locoFront"]) ? a.frontCoupler : a.rearCoupler;
Coupler cb = bool.Parse(ctx.Args["wagonFront"]) ? b.frontCoupler : b.rearCoupler;
yield return LocalPlayer.TeleportToGround(Dev.Abs(ca.transform.position).x + 2.5f, Dev.Abs(ca.transform.position).z);
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
var optimizers = new[] { a, b }.SelectMany(c => c.GetComponentsInChildren<ChainCouplerVisibilityOptimizer>(true)).ToList();
foreach (var o in optimizers) o.LockLOD();
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 15f && new[] { ca.ChainScript, cb.ChainScript }.Any(c => c == null || c.CurrentState == ChainCouplerInteraction.State.Disabled);) yield return null;
IEnumerator Pause() { for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < float.Parse(ctx.Args["pause"]);) yield return null; }
var steps = new List<string>();
var chain = ca.ChainScript; var other = cb.ChainScript;
if (chain.CurrentState != ChainCouplerInteraction.State.Parked && chain.CurrentState != ChainCouplerInteraction.State.Dangling) { chain = cb.ChainScript; other = ca.ChainScript; }
if (chain.CurrentState != ChainCouplerInteraction.State.Parked && chain.CurrentState != ChainCouplerInteraction.State.Dangling)
    throw new Exception("no free chain: " + ca.ChainScript.CurrentState + "/" + cb.ChainScript.CurrentState);
var knob = (GameObject)typeof(ChainCouplerInteraction).GetField("knob", flags).GetValue(chain);
// A player who misses the hook lets the chain dangle and tries again.
for (int attempt = 0; attempt < 3 && chain.CurrentState is not (ChainCouplerInteraction.State.Attached_Loose or ChainCouplerInteraction.State.Attached_Tight); attempt++)
{
    typeof(ChainCouplerInteraction).GetMethod("OnGrabbed", flags).Invoke(chain, new object[] { null });
    for (int i = 0; i < 40; i++) { knob.transform.position = other.ownAttachPoint.transform.position; yield return null; }
    typeof(ChainCouplerInteraction).GetMethod("OnUngrabbed", flags).Invoke(chain, new object[] { null });
    for (int i = 0; i < 30; i++) yield return null;
    if (attempt > 0) steps.Add("hook attempt " + (attempt + 1));
}
steps.Add("hooked: " + chain.CurrentState + ", coupled " + (ca.coupledTo == cb)); yield return Pause();
if (chain.CurrentState == ChainCouplerInteraction.State.Attached_Loose)
    typeof(ChainCouplerInteraction).GetMethod("OnScrewButtonUsed", flags).Invoke(chain, null);
for (int i = 0; i < 30; i++) yield return null;
steps.Add("tightened: " + chain.CurrentState); yield return Pause();
ca.ConnectAirHose(cb, true); steps.Add("hoses joined: " + (ca.GetAirHoseConnectedTo() == cb)); yield return Pause();
ca.IsCockOpen = true; cb.IsCockOpen = true; steps.Add("cocks open");
for (int i = 0; i < 30; i++) yield return null;
foreach (var o in optimizers) o.UnlockLOD();
ctx.Result = new { steps };
