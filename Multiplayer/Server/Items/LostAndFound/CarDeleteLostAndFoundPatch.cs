using HarmonyLib;

namespace Multiplayer.Server.Items.LostAndFound;

/// <summary>
/// The items on a car about to be deleted are dealt with first (<see cref="DeletedCarItems"/>),
/// after the players on it have been stepped off (CarDeletePlayerSafetyPatch, at the default
/// priority). Every way vanilla deletes a car (one car, a list of them at once or over a few frames,
/// everything on a track: LF5) prepares each car with <c>PrepareTrainCarForDeleting</c>, so that is
/// where its items come off. On the slow path vanilla prepares and hides one car every two frames: an
/// item taken off every car up front fell back into a car that was still there, and went with it (J1).
/// </summary>
[HarmonyPatch(typeof(CarSpawner))]
public static class CarDeleteLostAndFoundPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    [HarmonyPatch(nameof(CarSpawner.PrepareTrainCarForDeleting))]
    private static void PrepareTrainCarForDeleting(TrainCar trainCar)
    {
        // Already deleted: the server's prefix skips vanilla (Server/Trains/CarSpawnerPatch.cs).
        if (trainCar == null || trainCar.logicCar == null)
            return;
        DeletedCarItems.Collect(trainCar);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CarSpawner.ActuallyDeletingTrainCar))]
    private static void ActuallyDeletingTrainCar(TrainCar trainCar) => DeletedCarItems.Deleted(trainCar);
}
