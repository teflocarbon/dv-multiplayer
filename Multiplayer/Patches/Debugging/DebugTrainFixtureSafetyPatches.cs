#if DEBUG
using HarmonyLib;
using Multiplayer.Debugging.RuntimeTests.TrainFixtures;
using System.Collections.Generic;
using System.Reflection;

namespace Multiplayer.Patches.Debugging;

[HarmonyPatch]
internal static class DebugTrainFixtureBogieDerailPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(Bogie)).FindAll(method => method.Name == "Derail");

    private static bool Prefix(Bogie __instance) => !DebugTrainFixtureManager.IsProtected(__instance);
}

[HarmonyPatch(typeof(TrainCar), nameof(TrainCar.Derail))]
internal static class DebugTrainFixtureCarDerailPatch
{
    private static bool Prefix(TrainCar __instance) => !DebugTrainFixtureManager.IsProtected(__instance);
}

[HarmonyPatch]
internal static class DebugTrainFixtureJunctionLeasePatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(Junction)).FindAll(method => method.Name == "Switch");

    private static bool Prefix(Junction __instance) =>
        !DebugTrainFixtureManager.ShouldBlockJunctionMutation(__instance);
}
#endif
