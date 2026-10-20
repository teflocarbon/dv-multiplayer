using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Career;

[HarmonyPatch(typeof(CarsSaveManager), nameof(CarsSaveManager.Load))]
public static class CarsSaveManager_Load_Patch
{
    private static bool Prefix()
    {
        if (!NetworkRole.IsRemoteClient)
            return true;
        CarsSaveManager.DeleteAllExistingCars();
        return false;
    }
}
