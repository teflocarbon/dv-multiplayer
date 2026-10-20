using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;
using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Server.Session;

namespace Multiplayer.Server.Trains;

[HarmonyPatch(typeof(CarSpawner))]
public static class CarSpawner_Patch
{
    // A car deleted twice threw in DV's slow bulk delete: DeleteCar on a car that
    // DeleteTrainCarsFromTrack (the yard's cleanup, for one) still held. The throw stopped that
    // delete's coroutine, and every car it had already switched off was never deleted: each stayed
    // in its trainset with no NetworkedTrainCar, and the host refused every join until it restarted
    // (#84). A deleted car has no logic car until DV's pool hands it out again; skip it.
    private static bool AlreadyDeleted(TrainCar trainCar) => trainCar != null && trainCar.logicCar == null;

    // Why each car is being removed, worked out when DeleteCar or DeleteTrainCars is called: a bulk
    // delete prepares its cars later from a coroutine, where the caller is gone from the stack.
    private static readonly Dictionary<TrainCar, ChangeCause<CarRemovalReason>> removalCauses = new();

    [HarmonyPatch(nameof(CarSpawner.DeleteCar))]
    [HarmonyPrefix]
    private static void DeleteCar(TrainCar trainCar) => NoteRemovalCause(new List<TrainCar> { trainCar });

    [HarmonyPatch(nameof(CarSpawner.DeleteTrainCars))]
    [HarmonyPrefix]
    private static void DeleteTrainCars(List<TrainCar> trainCarsToDelete) => NoteRemovalCause(trainCarsToDelete);

    private static void NoteRemovalCause(List<TrainCar> trainCars)
    {
        if (trainCars == null || trainCars.Count == 0 || UnloadWatcher.isUnloading || NetworkLifecycle.Instance == null)
            return;
        if (removalCauses.Count > 512)
            removalCauses.Clear(); // causes for cars a delete skipped
        ChangeCause<CarRemovalReason> cause = RemovalCause();
        foreach (TrainCar trainCar in trainCars)
            if (trainCar != null)
                removalCauses[trainCar] = cause;
    }

    private static ChangeCause<CarRemovalReason> RemovalCause()
    {
        if (TrainChangeCauses.CurrentCause<CarRemovalReason>() is ChangeCause<CarRemovalReason> scoped)
            return scoped;
        List<MethodBase> callers = TrainChangeCauses.Callers(2);
        if (TrainChangeCauses.Called(callers, nameof(UnusedTrainCarDeleter)))
            return TrainChangeCauses.Cause(CarRemovalReason.Despawned);
        if (TrainChangeCauses.Called(callers, "StationProceduralJobGenerator"))
            return TrainChangeCauses.Cause(CarRemovalReason.JobGeneration);
        if (TrainChangeCauses.Called(callers, "LocoRestorationController"))
            return TrainChangeCauses.Cause(CarRemovalReason.LocoRestoration);
        if (TrainChangeCauses.Called(callers, "CarsSaveManager"))
            return TrainChangeCauses.Cause(CarRemovalReason.SaveLoad);
        // This peer's own player, through vanilla's deleter (a host playing).
        if (TrainChangeCauses.Called(callers, "CommsRadioCarDeleter"))
            return TrainChangeCauses.LocalCause(CarRemovalReason.PlayerDeleted);
        if (TrainChangeCauses.Called(callers, "GarageCarSpawner", "ReturnCarHome"))
            return TrainChangeCauses.Cause(CarRemovalReason.ReturnedToGarage);
        return TrainChangeCauses.Cause(CarRemovalReason.Unknown, detail: TrainChangeCauses.Describe(callers));
    }

    [HarmonyPatch(nameof(CarSpawner.PrepareTrainCarForDeleting))]
    [HarmonyPrefix]
    private static bool PrepareTrainCarForDeleting(TrainCar trainCar, out IDisposable __state)
    {
        __state = null;
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

        if (!removalCauses.TryGetValue(trainCar, out ChangeCause<CarRemovalReason> cause))
            cause = RemovalCause();
        removalCauses.Remove(trainCar);
        TrainChangeCauses.RecordRemoval(networkedTrainCar, cause);
        NetworkServer.Instance?.SendDestroyTrainCar(networkedTrainCar, cause);

        // Vanilla uncouples the car here; those uncouples are this removal's.
        __state = TrainChangeCauses.Scope(new ChangeCause<UncoupleReason>(UncoupleReason.CarRemoved,
            cause.PlayerId, cause.PlayerName, cause.Tick,
            $"{networkedTrainCar.CurrentID} {cause.Reason}" + (cause.Detail.Length > 0 ? $": {cause.Detail}" : string.Empty)));
        return true;
    }

    [HarmonyPatch(nameof(CarSpawner.PrepareTrainCarForDeleting))]
    [HarmonyFinalizer]
    private static Exception PrepareTrainCarForDeletingFinalizer(Exception __exception, IDisposable __state)
    {
        __state?.Dispose();
        return __exception;
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
        NetworkServer.Instance.SendSpawnTrainset(__result, true, true);
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
        NetworkServer.Instance.SendSpawnTrainset([__result], true, true);

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
        NetworkServer.Instance.SendSpawnTrainset([__result], true, true);

    }
}
