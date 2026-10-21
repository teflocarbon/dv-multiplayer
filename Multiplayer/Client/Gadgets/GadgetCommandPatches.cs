using DV.Customization.Gadgets.Implementations;
using HarmonyLib;
using Multiplayer.Shared.Gadgets;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// The buttons on installed gadgets (gadgets.md, "Gadget state"), caught where the gadget's LOD
/// button, its HUD button and anything else end up: vanilla's effect runs here at once, and
/// GadgetRunningClient sends it to the server.
/// </summary>
[HarmonyPatch]
internal static class GadgetCommandPatches
{
    // The distance tracker acknowledges itself from Update (a reversal, a power cut, past its reset
    // distance): that's running state, not a press.
    private static bool roadrunnerUpdating;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GadgetATSLOD), nameof(GadgetATSLOD.OnAckButton))]
    private static void AtsAcknowledge(GadgetATSLOD __instance) =>
        GadgetRunningClient.Pressed(__instance.Base, GadgetCommand.AtsAcknowledge);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetRoadrunner), "Update")]
    private static void RoadrunnerUpdateStart() => roadrunnerUpdating = true;

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(GadgetRoadrunner), "Update")]
    private static void RoadrunnerUpdateEnd() => roadrunnerUpdating = false;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GadgetRoadrunner), nameof(GadgetRoadrunner.StartMeasure))]
    private static void DistanceStart(GadgetRoadrunner __instance)
    {
        if (!roadrunnerUpdating)
            GadgetRunningClient.Pressed(__instance, GadgetCommand.DistanceStart);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GadgetRoadrunner), nameof(GadgetRoadrunner.Acknowledge))]
    private static void DistanceAcknowledge(GadgetRoadrunner __instance)
    {
        if (!roadrunnerUpdating)
            GadgetRunningClient.Pressed(__instance, GadgetCommand.DistanceAcknowledge);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GadgetDefectDetectorLOD), "Snooze")]
    private static void DefectSnooze(GadgetDefectDetectorLOD __instance) =>
        GadgetRunningClient.Pressed(__instance.Base, GadgetCommand.DefectSnooze);
}
