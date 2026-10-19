using DV.ServicePenalty;
using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Career;

/// <summary>
/// A remote client keeps no debts of its own: it knows only whether the server has any
/// (<see cref="HasDebt"/>, from its broadcast).
/// </summary>
[HarmonyPatch(typeof(CareerManagerDebtController))]
public static class CareerManagerDebtControllerPatch
{
    public static bool HasDebt;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CareerManagerDebtController.RegisterDebt))]
    private static bool RegisterDebt_Prefix()
    {
        return NetworkLifecycle.Instance.IsHost();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CareerManagerDebtController.UnregisterDebt))]
    private static bool UnregisterDebt_Prefix()
    {
        return NetworkLifecycle.Instance.IsHost();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CareerManagerDebtController.NumberOfNonZeroPricedDebts), MethodType.Getter)]
    private static bool NumberOfNonZeroPricedDebts_Prefix(ref int __result)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return true;
        __result = HasDebt ? 1 : 0;
        return false;
    }
}
