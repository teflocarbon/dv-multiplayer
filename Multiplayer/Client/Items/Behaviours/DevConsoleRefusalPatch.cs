using CommandTerminal;
using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Items.Behaviours;

/// <summary>
/// The dev console's item spawns and destroys on a client (OI-VAN-04; docs/design/items/vanilla-storage.md,
/// "The dev console"): items are the server's, and a client's spawn was never adopted (it vanished)
/// and its destroys took only its own copies. They are refused with a message; the host's run there
/// (the server's <c>DevConsolePatch</c>). Listing the item prefabs still works.
/// </summary>
[HarmonyPatch(typeof(DV.Console))]
public static class DevConsoleRefusalPatch
{
    private static bool Refused(string command)
    {
        if (NetworkLifecycle.Instance == null || !NetworkLifecycle.Instance.IsSessionRunning || !NetworkRole.IsRemoteClient)
            return false;
        Terminal.Log("{0}", new object[] { $"{command} changes the world's items, which are the server's in multiplayer: run it on the host" });
        return true;
    }

    [HarmonyPatch("Dev_SpawnItem")]
    [HarmonyPrefix]
    private static bool SpawnItem(CommandArg[] args) => args == null || args.Length == 0 || !Refused("Dev.SpawnItem");

    [HarmonyPatch("Dev_SpawnMoney")]
    [HarmonyPrefix]
    private static bool SpawnMoney() => !Refused("Dev.SpawnMoney");

    [HarmonyPatch("Debug_DestroyAllItems")]
    [HarmonyPrefix]
    private static bool DestroyAllItems() => !Refused("Debug.DestroyAllItems");

    [HarmonyPatch("Debug_DestroyBuggedObjects")]
    [HarmonyPrefix]
    private static bool DestroyBuggedObjects() => !Refused("Debug.DestroyBuggedObjects");

    [HarmonyPatch("Debug_DeleteCarsAndJobsSaveData")]
    [HarmonyPrefix]
    private static bool DeleteCarsAndJobsSaveData() => !Refused("Debug.DeleteCarsAndJobsSaveData");
}
