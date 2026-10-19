// class probe, host. Turns the server's own fall-rescue scan off (ServerItemManager.HostScanForFallenItems),
// so an item under the map is rescued only through a client's below-map report: the server loads
// terrain around remote players, and its own scan would usually find the item first.
// Args: action=install|remove.
using HarmonyLib;
public static class Probe
{
    const string Slot = "dvmp.client-fall-rescue.no-host-scan";
    public static bool Skip() => false;
    public static object Run(DevContext ctx)
    {
        var h = new Harmony(Slot);
        h.UnpatchAll(Slot);
        if (ctx.Args["action"] == "install")
            h.Patch(AccessTools.Method(typeof(Multiplayer.Server.Items.ServerItemManager), "HostScanForFallenItems"),
                prefix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(Skip))));
        return new { installed = ctx.Args["action"] == "install" };
    }
}
