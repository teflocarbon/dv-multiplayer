// Host: the host's player works the screw. On the chain on the hook: loosen it and tighten it again,
// twice, through its screw button (vanilla's OnScrewButtonUsed, which the mod's postfix reports).
// Then the Start path: the postfix seeing the screw used in a state it doesn't expect, here on the
// other car's chain, which isn't on the hook. On the host that must change nothing: before stage 1
// the server applied it to the host's own coupler, locked its screw button and left the coupler
// marked in use. Returns the steps.
var a = Trains.Find(ctx.Args["loco"]); var b = Trains.Find(ctx.Args["wagon"]);
Coupler ca = bool.Parse(ctx.Args["locoFront"]) ? a.frontCoupler : a.rearCoupler;
Coupler cb = bool.Parse(ctx.Args["wagonFront"]) ? b.frontCoupler : b.rearCoupler;
yield return LocalPlayer.TeleportToGround(Dev.Abs(ca.transform.position).x + 2.5f, Dev.Abs(ca.transform.position).z);
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
// Chains only switch on when a camera renders them; the test games are minimised, so lock them on.
var optimizers = new[] { a, b }.SelectMany(c => c.GetComponentsInChildren<ChainCouplerVisibilityOptimizer>(true)).ToList();
foreach (var o in optimizers) o.LockLOD();
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 15f && new[] { ca.ChainScript, cb.ChainScript }.Any(c => c == null || c.CurrentState == ChainCouplerInteraction.State.Disabled);) yield return null;
var steps = new List<string>();
var hooked = new[] { ca.ChainScript, cb.ChainScript }.FirstOrDefault(c => c != null && c.CurrentState is ChainCouplerInteraction.State.Attached_Tight or ChainCouplerInteraction.State.Attached_Loose);
if (hooked == null) throw new Exception("no chain on the hook: " + ca.ChainScript?.CurrentState + "/" + cb.ChainScript?.CurrentState);
var screw = typeof(ChainCouplerInteraction).GetMethod("OnScrewButtonUsed", flags);
for (int round = 1; round <= 2; round++)
{
    foreach (var wanted in new[] { ChainCouplerInteraction.State.Attached_Loose, ChainCouplerInteraction.State.Attached_Tight })
    {
        if (hooked.CurrentState != wanted)
            screw.Invoke(hooked, null);
        for (float t0 = Time.realtimeSinceStartup; hooked.CurrentState != wanted && Time.realtimeSinceStartup - t0 < 5f;) yield return null;
        steps.Add($"round {round}: {wanted} -> {hooked.CurrentState}");
        for (int i = 0; i < 30; i++) yield return null;
    }
}
// The Start path, as Harmony runs the postfix after the screw button's handler.
var free = hooked == ca.ChainScript ? cb.ChainScript : ca.ChainScript;
var postfix = AppDomain.CurrentDomain.GetAssemblies().Select(asm => asm.GetType("Multiplayer.Client.Trains.ChainCouplerInteractionPatch")).First(type => type != null)
    .GetMethod("OnScrewButtonUsed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
if (free != null)
{
    postfix.Invoke(null, new object[] { free });
    steps.Add("Start path on the other chain (" + free.CurrentState + ")");
}
else
{
    steps.Add("no second chain: Start path not exercised");
}
for (int i = 0; i < 60; i++) yield return null;
foreach (var o in optimizers) o.UnlockLOD();
ctx.Result = new { steps, chain = hooked == ca.ChainScript ? "loco's" : "wagon's" };
