using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.Server.Items;

namespace Multiplayer.Server.Trains;

/// <summary>
/// The items lying in a car about to be deleted go to Lost and Found
/// (<see cref="NetworkedLostAndFoundManager.CollectFromDeletedCar"/>), after the players on it have
/// been stepped off (CarDeletePlayerSafetyPatch, at the default priority).
/// </summary>
[HarmonyPatch(typeof(CarSpawner))]
public static class CarDeleteLostAndFoundPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    [HarmonyPatch(nameof(CarSpawner.DeleteCar))]
    private static void DeleteCar(TrainCar trainCar) => NetworkedLostAndFoundManager.CollectFromDeletedCar(trainCar);

    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    [HarmonyPatch(nameof(CarSpawner.DeleteTrainCars))]
    private static void DeleteTrainCars(List<TrainCar> trainCarsToDelete)
    {
        if (trainCarsToDelete == null)
            return;
        foreach (TrainCar car in trainCarsToDelete)
            NetworkedLostAndFoundManager.CollectFromDeletedCar(car);
    }
}
