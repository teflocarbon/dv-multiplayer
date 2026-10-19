using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Career;

[HarmonyPatch(typeof(CarsSaveManager), nameof(CarsSaveManager.Load))]
public static class CarsSaveManager_Load_Patch
{
    private static bool Prefix()
    {
        if (!NetworkLifecycle.Instance.IsClientRunning || NetworkLifecycle.Instance.IsHost())
            return true;
        CarsSaveManager.DeleteAllExistingCars();
        return false;
    }
}
