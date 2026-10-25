using HarmonyLib;
using Multiplayer.Shared.Players;
using UnityEngine;

namespace Multiplayer.Shared.Trains;

/// <summary>
/// A player standing in or on a car is parented to it. Deleted cars go back to the game's pool,
/// taking anything parented to them along, and vanilla only rescues the player when a car's
/// interior is destroyed. In multiplayer, cars are deleted under players by others: the host
/// deleting or despawning a car a client is standing in left that client with no player object
/// at all. Detach the local player (the same recovery vanilla uses) and any remote players'
/// avatars before a car is deleted: when vanilla prepares it, which every way of deleting a car does, one
/// car at a time (a track's cars too, which DeleteCar and DeleteTrainCars never saw). The items on
/// it come off after that, on the host (Server/Items/LostAndFound/CarDeleteLostAndFoundPatch.cs).
/// </summary>
[HarmonyPatch(typeof(CarSpawner))]
public static class CarDeletePlayerSafetyPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(CarSpawner.PrepareTrainCarForDeleting))]
    private static void PrepareTrainCarForDeleting(TrainCar trainCar) => Evacuate(trainCar);

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
