using DV.ServicePenalty;
using HarmonyLib;

namespace Multiplayer.Server.Career;

/// <summary>
/// The server broadcasts whether there is debt (NetworkedCareerManagerDebtController); a remote
/// client's debt controller follows it (Client/Career/CareerManagerDebtControllerPatch).
/// </summary>
[HarmonyPatch(typeof(CareerManagerDebtController))]
public static class CareerManagerDebtControllerServerPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(CareerManagerDebtController.Awake))]
    private static void Awake(CareerManagerDebtController __instance)
    {
        __instance.gameObject.AddComponent<NetworkedCareerManagerDebtController>();
    }
}
