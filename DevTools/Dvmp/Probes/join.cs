// Joins a server from the main menu as a distinct test identity.
// Args: address, port, password, name, guid.
// The identity is overridden with in-memory Harmony patches on Settings.GetUserName/GetGuid
// rather than by editing Settings, because both game instances share one Settings.xml and
// the mod writes it back to disk.
using System.Reflection;
using HarmonyLib;
using Multiplayer.Shared.Session;
using MpSettings = Multiplayer.Settings;

public static class Probe
{
    private static string name;
    private static System.Guid guid;

    public static object Run(DevContext ctx)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance ?? throw new Exception("NetworkLifecycle missing (is Multiplayer loaded?)");
        if (lifecycle.IsServerRunning || lifecycle.IsClientRunning)
            return new { alreadyRunning = true, role = ctx.Role };
        if (UnityEngine.Object.FindObjectOfType<DV.UI.MainMenuController>() == null)
            throw new Exception("main menu not ready");

        string address = Arg(ctx, "address", "127.0.0.1");
        int port = int.Parse(Arg(ctx, "port", "7777"));
        string password = Arg(ctx, "password", string.Empty);
        name = Arg(ctx, "name", "DVMP-Client");
        guid = System.Guid.Parse(Arg(ctx, "guid", "00000000-0000-0000-0000-00000000c11e"));

        Harmony harmony = new("dvmp.harness.identity");
        harmony.UnpatchAll("dvmp.harness.identity");
        harmony.Patch(AccessTools.Method(typeof(MpSettings), nameof(MpSettings.GetUserName)),
            prefix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(UserName), BindingFlags.NonPublic | BindingFlags.Static)));
        harmony.Patch(AccessTools.Method(typeof(MpSettings), nameof(MpSettings.GetGuid)),
            prefix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(Guid), BindingFlags.NonPublic | BindingFlags.Static)));

        DevContext.State["dvmp.join.disconnect"] = null;
        lifecycle.StartClient(address, port, password, false,
            (reason, detail) => DevContext.State["dvmp.join.disconnect"] = reason + ": " + detail);

        return new { address, port, name, guid = guid.ToString() };
    }

    private static bool UserName(ref string __result) { __result = name; return false; }
    private static bool Guid(ref System.Guid __result) { __result = guid; return false; }

    private static string Arg(DevContext ctx, string key, string fallback) =>
        ctx.Args.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value) ? value : fallback;
}
