using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Career;

[HarmonyPatch(typeof(UnlockablesManager), nameof(UnlockablesManager.UnlockInArray))]
public static class UnlockablesManagerPatch
{
    private static bool Prefix()
    {
        return NetworkLifecycle.Instance.IsHost();
    }
}
