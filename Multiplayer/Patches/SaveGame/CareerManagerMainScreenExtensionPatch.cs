using DV.ServicePenalty.UI;
using HarmonyLib;
using Multiplayer.Components.UI.CareerManager;

namespace Multiplayer.Patches.SaveGame;

[HarmonyPatch(typeof(CareerManagerMainScreen))]
internal static class CareerManagerMainScreenExtensionPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(CareerManagerMainScreen.Awake))]
    private static void AwakePostfix(CareerManagerMainScreen __instance)
    {
        CareerManagerExtensionHost host = __instance.GetComponent<CareerManagerExtensionHost>() ??
            __instance.gameObject.AddComponent<CareerManagerExtensionHost>();
        host.Initialize(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CareerManagerMainScreen.Activate))]
    private static void ActivatePostfix(CareerManagerMainScreen __instance) =>
        __instance.GetComponent<CareerManagerExtensionHost>()?.ShowMainRow();

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CareerManagerMainScreen.Disable))]
    private static void DisablePostfix(CareerManagerMainScreen __instance) =>
        __instance.GetComponent<CareerManagerExtensionHost>()?.ClearMainRow();

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CareerManagerMainScreen.HandleInputAction))]
    private static bool InputPrefix(CareerManagerMainScreen __instance, InputAction input)
    {
        if (input != InputAction.Confirm || __instance.selector.Current != 4)
            return true;
        __instance.GetComponent<CareerManagerExtensionHost>()?.OpenHub();
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CareerManagerMainScreen.GetCurrentSelection))]
    private static void SelectionPostfix(CareerManagerMainScreen __instance, ref string __result)
    {
        if (__instance.selector.Current == 4) __result = "MULTIPLAYER";
    }
}
