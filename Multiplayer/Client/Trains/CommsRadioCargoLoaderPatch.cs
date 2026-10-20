using DV;
using DV.ThingTypes;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.Trains;

/// <summary>
/// The sandbox cargo loader changed the cargo of the client's copy of the car only: the host's
/// car stayed as it was, so nothing was filled for anyone else and the client's cargo was
/// overwritten by the next host update. On a client, ask the host to do it; the host's cargo sync
/// then shows the result to everyone.
/// </summary>
[HarmonyPatch(typeof(CommsRadioCargoLoader))]
public static class CommsRadioCargoLoaderPatch
{
    private static readonly AccessTools.FieldRef<CommsRadioCargoLoader, TrainCar> PointedCar =
        AccessTools.FieldRefAccess<CommsRadioCargoLoader, TrainCar>("pointedCar");
    private static readonly AccessTools.FieldRef<CommsRadioCargoLoader, CargoType_v2> SelectedCargoType =
        AccessTools.FieldRefAccess<CommsRadioCargoLoader, CargoType_v2>("selectedCargoType");

    [HarmonyPrefix]
    [HarmonyPatch("LoadPointedCar")]
    private static bool LoadPointedCar(CommsRadioCargoLoader __instance) => RequestFromHost(__instance, load: true);

    [HarmonyPrefix]
    [HarmonyPatch("UnloadPointedCar")]
    private static bool UnloadPointedCar(CommsRadioCargoLoader __instance) => RequestFromHost(__instance, load: false);

    private static bool RequestFromHost(CommsRadioCargoLoader loader, bool load)
    {
        if (!NetworkRole.IsRemoteClient)
            return true;
        TrainCar car = PointedCar(loader);
        if (car == null || !car.TryNetworked(out NetworkedTrainCar networkedCar))
            return false;
        uint cargoNetId = 0;
        if (load && !CargoTypeLookup.Instance.TryGetNetId(SelectedCargoType(loader), out cargoNetId))
        {
            Multiplayer.LogWarning($"Sandbox cargo loader: no network id for cargo {SelectedCargoType(loader)?.id}");
            return false;
        }
        NetworkClient.Instance.SendSandboxCargo(networkedCar.NetId, cargoNetId);
        CommsRadioController.PlayAudioFromRadio(loader.confirmSound, loader.transform);
        return false;
    }
}
