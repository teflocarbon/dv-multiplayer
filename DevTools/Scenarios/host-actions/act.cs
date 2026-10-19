// Acting peer: one player action, the way the game does it. Args: action (junction: throw it to
// its next branch; turntable: push it round for 2 s, as the lever does, and let it snap; fast-travel:
// skip time as fast travel does; sleep: sleep in a bed), junction, turntable (NetIds), seconds (the
// time skip).
string action = ctx.Args["action"];
float seconds = ctx.Args.TryGetValue("seconds", out string s) ? float.Parse(s) : 3600f;
if (action == "junction")
{
    NetworkedJunction.Get(ushort.Parse(ctx.Args["junction"]), out NetworkedJunction j);
    j.Junction.Switch(Junction.SwitchMode.REGULAR);
    yield return new WaitForSecondsRealtime(0.5f);
    ctx.Result = new { branch = (int)j.Junction.selectedBranch };
}
else if (action == "turntable")
{
    NetworkedTurntable.Get(byte.Parse(ctx.Args["turntable"]), out NetworkedTurntable t);
    var controller = UnityEngine.Object.FindObjectsOfType<TurntableController>().First(c => c.turntable == t.TurntableRailTrack);
    float before = t.TurntableRailTrack.targetYRotation;
    controller.pushingPositiveDirectionValue = 1f;
    yield return new WaitForSecondsRealtime(2f);
    controller.pushingPositiveDirectionValue = 0f;
    // It snaps to the nearest track at 10 degrees a second.
    float last = float.NaN;
    for (int i = 0; i < 40 && !Mathf.Approximately(last, t.TurntableRailTrack.targetYRotation); i++)
    {
        last = t.TurntableRailTrack.targetYRotation;
        yield return new WaitForSecondsRealtime(0.5f);
    }
    ctx.Result = new { before, rotation = t.TurntableRailTrack.targetYRotation };
}
else if (action == "fast-travel")
{
    DV.TimeAdvance.AdvanceTime(seconds);
    yield return new WaitForSecondsRealtime(0.5f);
    ctx.Result = new { skipped = seconds };
}
else if (action == "sleep")
{
    var sleep = DV.BedSleepingController.Instance;
    sleep.Sleep(seconds, 0.2f, 0.2f, null);
    yield return new WaitForSecondsRealtime(0.5f);
    for (int i = 0; i < 60 && sleep.IsSleeping; i++)
        yield return new WaitForSecondsRealtime(0.5f);
    ctx.Result = new { skipped = seconds, stillSleeping = sleep.IsSleeping };
}
else
    throw new Exception("unknown action " + action);
