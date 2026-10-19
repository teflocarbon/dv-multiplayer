using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Career;

[HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.SaveAllowed))]
public static class SaveGameManager_SaveAllowed_Patch
{
    private static bool Prefix(ref bool __result)
    {
        if (!NetworkLifecycle.Instance.IsClientRunning || NetworkLifecycle.Instance.IsHost())
            return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.UpdateInternalData))]
public static class SaveGameManager_UpdateInternalData_Patch
{
    private static void Postfix(SaveGameManager __instance)
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        NetworkedSaveGameManager.Instance.Server_UpdateInternalData(__instance.data);
    }
}
