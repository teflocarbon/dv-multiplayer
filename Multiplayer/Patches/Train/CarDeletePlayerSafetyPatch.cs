using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.Components.Networking.Player;
using UnityEngine;

namespace Multiplayer.Patches.Train;

/// <summary>
/// A player standing in or on a car is parented to it. Deleted cars go back to the game's pool,
/// taking anything parented to them along, and vanilla only rescues the player when a car's
/// interior is destroyed. In multiplayer, cars are deleted under players by others: the host
/// deleting or despawning a car a client is standing in left that client with no player object
/// at all. Detach the local player (the same recovery vanilla uses) and any remote players'
/// avatars before a car is deleted.
/// </summary>
[HarmonyPatch(typeof(CarSpawner))]
public static class CarDeletePlayerSafetyPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(CarSpawner.DeleteCar))]
    private static void DeleteCar(TrainCar trainCar) => Evacuate(trainCar);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CarSpawner.DeleteTrainCars))]
    private static void DeleteTrainCars(List<TrainCar> trainCarsToDelete)
    {
        if (trainCarsToDelete == null)
            return;
        foreach (TrainCar car in trainCarsToDelete)
            Evacuate(car);
    }

    internal static void Evacuate(TrainCar car)
    {
        if (car == null)
            return;

        Transform player = PlayerManager.PlayerTransform;
        if (player != null && (PlayerManager.Car == car ||
                               (player.parent != null && TrainCar.Resolve(player.parent) == car)))
        {
            Multiplayer.LogWarning($"Car {car.ID} is being deleted with the local player on it; stepping off first");
            if (player.TryGetComponent(out CharacterReparenting reparenting))
                reparenting.ReparentTo(null);
            if (player.TryGetComponent(out CustomFirstPersonController controller))
                controller.MoveBy(Vector3.zero);
            if (PlayerManager.Car == car)
                PlayerManager.SetCar(null);
        }

        foreach (NetworkedPlayer avatar in car.GetComponentsInChildren<NetworkedPlayer>(true))
            avatar.UpdateCar(0);
    }
}
