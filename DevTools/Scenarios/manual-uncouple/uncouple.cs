// Client: decouple by hand, a step a second like a player: close both angle cocks, part the
// hoses, loosen the screw, then lift the chain off and let it go.
var a = Trains.Find(ctx.Args["loco"]); var b = Trains.Find(ctx.Args["wagon"]);
Coupler ca = bool.Parse(ctx.Args["locoFront"]) ? a.frontCoupler : a.rearCoupler;
Coupler cb = bool.Parse(ctx.Args["wagonFront"]) ? b.frontCoupler : b.rearCoupler;
yield return LocalPlayer.TeleportToGround(Dev.Abs(ca.transform.position).x + 2.5f, Dev.Abs(ca.transform.position).z);
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
var steps = new List<string>();
// Chains only switch on when a camera renders them; the test games are minimised, so lock them on.
var optimizers = new[] { a, b }.SelectMany(c => c.GetComponentsInChildren<ChainCouplerVisibilityOptimizer>(true)).ToList();
foreach (var o in optimizers) o.LockLOD();
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 15f && new[] { ca.ChainScript, cb.ChainScript }.All(c => c == null || c.CurrentState == ChainCouplerInteraction.State.Disabled);) yield return null;
IEnumerator Pause() { for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < float.Parse(ctx.Args["pause"]);) yield return null; }
ca.IsCockOpen = false; cb.IsCockOpen = false; steps.Add("cocks closed"); yield return Pause();
ca.DisconnectAirHose(true); steps.Add("hoses parted: " + (ca.GetAirHoseConnectedTo() == null)); yield return Pause();
// The chain hanging on the hook belongs to one side; its screw button loosens it.
var chain = new[] { ca.ChainScript, cb.ChainScript }.FirstOrDefault(c => c != null && c.CurrentState is ChainCouplerInteraction.State.Attached_Tight or ChainCouplerInteraction.State.Attached_Loose);
if (chain == null) throw new Exception("no chain on the hook: " + ca.ChainScript?.CurrentState + "/" + cb.ChainScript?.CurrentState);
if (chain.CurrentState == ChainCouplerInteraction.State.Attached_Tight)
    typeof(ChainCouplerInteraction).GetMethod("OnScrewButtonUsed", flags).Invoke(chain, null);
for (int i = 0; i < 30; i++) yield return null;
steps.Add("loosened: " + chain.CurrentState); yield return Pause();
typeof(ChainCouplerInteraction).GetMethod("OnGrabbed", flags).Invoke(chain, new object[] { null });
for (int i = 0; i < 30; i++) yield return null;
steps.Add("lifted: " + chain.CurrentState + ", coupled " + (ca.coupledTo != null));
// Carry the chain to its parking hook, as a player does before letting go.
var knob = (GameObject)typeof(ChainCouplerInteraction).GetField("knob", flags).GetValue(chain);
for (int i = 0; i < 40; i++) { knob.transform.position = chain.parkedAnchor.position; yield return null; }
steps.Add("at park hook: ring " + Vector3.Distance(chain.chainRingAnchor.position, chain.parkedAnchor.position).ToString("F2") + " m away");
typeof(ChainCouplerInteraction).GetMethod("OnUngrabbed", flags).Invoke(chain, new object[] { null });
for (int i = 0; i < 30; i++) yield return null;
steps.Add("let go: " + chain.CurrentState + ", coupled " + (ca.coupledTo != null));
foreach (var o in optimizers) o.UnlockLOD();
ctx.Result = new { steps };
