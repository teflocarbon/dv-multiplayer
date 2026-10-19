// Either peer: what the scenario compares. The junction's branch and how many times it switched
// since watch=1 (each switch raises Junction.Switched once, so a change applied twice counts 2),
// the turntable's rotation, the game clock and the shared sleep cooldown's last wake time.
// Args: junction, turntable (NetIds), watch (1: start counting the junction's switches from 0).
ushort jid = ushort.Parse(ctx.Args["junction"]);
byte tid = byte.Parse(ctx.Args["turntable"]);
NetworkedJunction.Get(jid, out NetworkedJunction j);
NetworkedTurntable.Get(tid, out NetworkedTurntable t);
string key = "dvmp.host-actions.switched." + jid;
if (j != null && ctx.Args.TryGetValue("watch", out string watch) && watch == "1")
{
    if (!DevContext.State.ContainsKey(key + ".hooked"))
    {
        j.Junction.Switched += (mode, branch) =>
            DevContext.State[key] = (DevContext.State.TryGetValue(key, out object n) ? (int)n : 0) + 1;
        DevContext.State[key + ".hooked"] = true;
    }
    DevContext.State[key] = 0;
}
var sleep = DV.BedSleepingController.Instance;
return new
{
    branch = j == null ? -1 : (int)j.Junction.selectedBranch,
    switched = DevContext.State.TryGetValue(key, out object count) ? (int)count : 0,
    rotation = t == null ? -1f : t.TurntableRailTrack.targetYRotation,
    clock = DV.WeatherSystem.WeatherDriver.Instance.manager.RealDateTime.ToOADate(),
    wake = sleep == null ? 0 : sleep.lastWakeTime.ToOADate(),
    sleeping = sleep != null && sleep.IsSleeping,
};
