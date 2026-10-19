using DV;
using HarmonyLib;

namespace Multiplayer.Server.Coverage;

/// <summary>
/// On a multiplayer server, DV's freeze for derailed cars (kinematic until the car's Near and Far
/// tiles and terrain cell are all loaded) also lets a car move where real ground is under it
/// (ServerWorldCoverage.MayMove). The server keeps every Near tile loaded, so ballast is under
/// derailed cars far from its local player. Clients keep vanilla here: their derailed cars follow
/// the host (NetworkedTrainCar.UpdateDerailedKinematic).
/// </summary>
[HarmonyPatch(typeof(DerailedTrainCarIsKinematicHandler), "UpdateIsKinematicDependingOnLoadedCells")]
public static class DerailedTrainCarIsKinematicHandlerPatch
{
    private static bool Prefix(DerailedTrainCarIsKinematicHandler __instance)
    {
        if (ServerWorldCoverage.Instance == null || __instance.train == null)
            return true;
        ServerWorldCoverage.UpdateDerailedKinematic(__instance.train);
        return false;
    }
}
