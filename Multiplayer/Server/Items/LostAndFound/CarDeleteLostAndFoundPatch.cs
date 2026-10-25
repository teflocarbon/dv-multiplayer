using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace Multiplayer.Server.Items.LostAndFound;

/// <summary>
/// The items on a car about to be deleted are dealt with first (<see cref="DeletedCarItems"/>),
/// after the players on it have been stepped off (CarDeletePlayerSafetyPatch, at the default
/// priority). Every way vanilla deletes a car: one car, a list of them (at once or over a few
/// frames), and everything on a track (LF5).
/// </summary>
[HarmonyPatch(typeof(CarSpawner))]
public static class CarDeleteLostAndFoundPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    [HarmonyPatch(nameof(CarSpawner.DeleteCar))]
    private static void DeleteCar(TrainCar trainCar) => DeletedCarItems.Collect(trainCar);

    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    [HarmonyPatch(nameof(CarSpawner.DeleteTrainCars))]
    private static void DeleteTrainCars(List<TrainCar> trainCarsToDelete)
    {
        if (trainCarsToDelete == null)
            return;
        foreach (TrainCar car in trainCarsToDelete)
            DeletedCarItems.Collect(car);
    }

    // Starts vanilla's deleting coroutine itself, so neither prefix above sees its cars.
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    [HarmonyPatch(nameof(CarSpawner.DeleteTrainCarsFromTrack))]
    private static void DeleteTrainCarsFromTrack(RailTrack railTrack)
    {
        if (railTrack == null)
            return;
        foreach (TrainCar car in railTrack.BogiesOnTrack().Select(bogie => bogie.Car).Where(car => car != null).Distinct().ToList())
            DeletedCarItems.Collect(car);
    }
}
