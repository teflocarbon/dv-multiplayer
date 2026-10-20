using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Career;

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
