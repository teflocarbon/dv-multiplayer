using DV;
using DV.ThingTypes;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Utils;

namespace Multiplayer.Patches.CommsRadio;

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

    private static bool IsClient =>
        NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.IsClientRunning && !NetworkLifecycle.Instance.IsHost();

    [HarmonyPrefix]
    [HarmonyPatch("LoadPointedCar")]
    private static bool LoadPointedCar(CommsRadioCargoLoader __instance) => RequestFromHost(__instance, load: true);

    [HarmonyPrefix]
    [HarmonyPatch("UnloadPointedCar")]
    private static bool UnloadPointedCar(CommsRadioCargoLoader __instance) => RequestFromHost(__instance, load: false);

    private static bool RequestFromHost(CommsRadioCargoLoader loader, bool load)
    {
        if (!IsClient)
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
        NetworkLifecycle.Instance.Client.SendSandboxCargo(networkedCar.NetId, cargoNetId);
        CommsRadioController.PlayAudioFromRadio(loader.confirmSound, loader.transform);
        return false;
    }
}
