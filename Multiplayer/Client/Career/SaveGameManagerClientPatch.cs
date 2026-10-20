using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Career;

/// <summary>Only the host saves: a client never does.</summary>
[HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.SaveAllowed))]
public static class SaveGameManager_SaveAllowed_Patch
{
    private static bool Prefix(ref bool __result)
    {
        if (!NetworkRole.IsRemoteClient)
            return true;
        __result = false;
        return false;
    }
}
