// Acting peer: one player action, the way the game does it. Args: action (junction: throw it to
// its next branch; turntable: turn it half a turn with its controller; fast-travel:
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
    var controller = Resources.FindObjectsOfTypeAll<TurntableController>().First(c => c.turntable == t.TurntableRailTrack);
    float before = t.TurntableRailTrack.targetYRotation;
    // Half a turn lines the other end up with the same tracks. Pushing the handle can't be faked:
    // the controller's Update clears the push every frame unless a player's body is at the handle.
    controller.SetAngle(TurntableRailTrack.AngleRange0To360(before + 180f), forceNoSnapping: true);
    // NetworkedTurntable sends the new rotation from the next tick.
    yield return new WaitForSecondsRealtime(1f);
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
