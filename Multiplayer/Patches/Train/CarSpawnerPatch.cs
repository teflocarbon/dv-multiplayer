using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Utils;
using System.Collections.Generic;

namespace Multiplayer.Patches.Train;

[HarmonyPatch(typeof(CarSpawner))]
public static class CarSpawner_Patch
{
    // A car deleted twice threw in DV's slow bulk delete: DeleteCar on a car that
    // DeleteTrainCarsFromTrack (the yard's cleanup, for one) still held. The throw stopped that
    // delete's coroutine, and every car it had already switched off was never deleted: each stayed
    // in its trainset with no NetworkedTrainCar, and the host refused every join until it restarted
    // (#84). A deleted car has no logic car until DV's pool hands it out again; skip it.
    private static bool AlreadyDeleted(TrainCar trainCar) => trainCar != null && trainCar.logicCar == null;

    [HarmonyPatch(nameof(CarSpawner.PrepareTrainCarForDeleting))]
    [HarmonyPrefix]
    private static bool PrepareTrainCarForDeleting(TrainCar trainCar)
    {
        if (AlreadyDeleted(trainCar))
        {
            Multiplayer.LogWarning($"{trainCar.name} is already deleted, not deleting it again");
            return false;
        }

        if (UnloadWatcher.isUnloading)
            return true;

        if (trainCar == null || !trainCar.TryNetworked(out NetworkedTrainCar networkedTrainCar))
            return true;

        networkedTrainCar.IsDestroying = true;

        NetworkLifecycle.Instance.Server?.SendDestroyTrainCar(networkedTrainCar);
        return true;
    }

    [HarmonyPatch(nameof(CarSpawner.ActuallyDeletingTrainCar))]
    [HarmonyPrefix]
    private static bool ActuallyDeletingTrainCar(TrainCar trainCar)
    {
        // Returning a deleted car to the pool again would let the pool hand the same car out twice.
        return !AlreadyDeleted(trainCar);
    }

    [HarmonyPatch(nameof(CarSpawner.SpawnCars))]
    [HarmonyPostfix]
    private static void SpawnCars(List<TrainCar> __result)
    {
        if (UnloadWatcher.isUnloading)
            return;

        if (!NetworkLifecycle.Instance.IsHost())
            return;

        if (__result == null || __result.Count == 0)
            return;

        // Coupling is delayed by AutoCouple(), so a true trainset for the entire consist doesn't exist yet
        Multiplayer.LogDebug(() => $"SpawnCars() {__result?.Count} cars spawned, sending to players");
        NetworkLifecycle.Instance.Server.SendSpawnTrainset(__result, true, true);
    }

    [HarmonyPatch(nameof(CarSpawner.SpawnCarFromRemote))]
    [HarmonyPostfix]
    private static void SpawnCarFromRemote(TrainCar __result)
    {
        if (UnloadWatcher.isUnloading)
            return;

        if (!NetworkLifecycle.Instance.IsHost())
            return;

        if (__result == null)
            return;

        Multiplayer.LogDebug(() => $"SpawnCarFromRemote() {__result?.carLivery?.name} spawned, sending to players");
        NetworkLifecycle.Instance.Server.SendSpawnTrainset([__result], true, true);

    }

    [HarmonyPatch(nameof(CarSpawner.SpawnCarOnClosestTrack))]
    [HarmonyPostfix]
    private static void SpawnCarOnClosestTrack(TrainCar __result)
    {
        if (UnloadWatcher.isUnloading)
            return;

        if (!NetworkLifecycle.Instance.IsHost())
            return;

        if (__result == null)
            return;

        Multiplayer.LogDebug(() => $"SpawnCarOnClosestTrack() {__result?.carLivery?.name} spawned, sending to players");
        NetworkLifecycle.Instance.Server.SendSpawnTrainset([__result], true, true);

    }
}
