using DV.ServicePenalty.UI;
using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Career;

/// <summary>
/// A client has no fee list (only whether there are fees: CareerManagerDebtControllerPatch.HasDebt),
/// so its fees screen offers to pay them all instead. The server pays them for an admin
/// (ChatManager.PayFees) and says the outcome in chat.
/// </summary>
[HarmonyPatch(typeof(CareerManagerFeesScreen), nameof(CareerManagerFeesScreen.Activate))]
public static class CareerManagerFeesScreenPatch
{
    private static bool Prefix(CareerManagerFeesScreen __instance)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return true;
        if (!CareerManagerDebtControllerPatch.HasDebt)
        {
            __instance.infoScreen.SetInfoData(__instance.mainScreen, CareerManagerLocalization.FEES, CareerManagerLocalization.NO_FEES_CAN_BUY_LICENSES);
            __instance.screenSwitcher.SetActiveDisplay(__instance.infoScreen);
            return false;
        }
        __instance.confirmActionScreen.SetCustomActionData(
            () => NetworkLifecycle.Instance.Client.SendPayFeesRequest(),
            __instance.mainScreen,
            Locale.CAREER_MANAGER__PAY_FEES_TITLE,
            Locale.CAREER_MANAGER__PAY_FEES_INFO);
        __instance.screenSwitcher.SetActiveDisplay(__instance.confirmActionScreen);
        return false;
    }
}
