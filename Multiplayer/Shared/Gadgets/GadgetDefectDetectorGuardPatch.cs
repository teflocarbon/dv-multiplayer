using DV.Customization.Gadgets.Implementations;
using HarmonyLib;

namespace Multiplayer.Shared.Gadgets;

/// <summary>
/// A defect detector still installed on a car being deleted (the change log's forced removals usually
/// come first, but the car's deletion can overtake them) hears the car leave its train and reads the train that's gone:
/// vanilla's NullReferenceException there broke off the client's handler for the car's deletion.
/// With no train there are no cars to watch.
/// </summary>
[HarmonyPatch(typeof(GadgetDefectDetectorLOD))]
internal static class GadgetDefectDetectorGuardPatch
{
    [HarmonyPrefix]
    [HarmonyPatch("AfterTrainSetChange")]
    private static bool AfterTrainSetChange(GadgetDefectDetectorLOD __instance) => HasTrain(__instance);

    [HarmonyPrefix]
    [HarmonyPatch("BeforeTrainSetChange")]
    private static bool BeforeTrainSetChange(GadgetDefectDetectorLOD __instance) => HasTrain(__instance);

    private static bool HasTrain(GadgetDefectDetectorLOD lod) => lod.Base?.TrainCar?.trainset?.cars != null;
}
