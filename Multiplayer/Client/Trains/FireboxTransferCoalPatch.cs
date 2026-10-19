using DV.Simulation.Cars;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;

namespace Multiplayer.Client.Trains;

/// <summary>
/// Coal a player puts in a firebox. Shovelling goes through FireboxSimController.TransferCoal,
/// which a client reports to the host (it adds the same coal there).
///
/// This used to watch the firebox's coal input port instead, which a mechanical stoker writes too
/// (Custom Car Loader's SteamMechanicalStoker, on the S484 and the 2901, every simulation tick on
/// every game). Every game reported its own stoker's coal, including the host's own client, and
/// the host added each report on top of its own stoker: the firebox sat overfull, its overfill
/// damage climbed, and the coal flickered (playtest 2: 47,000 reports in one evening's log).
/// The host never reports: it applies its own coal.
/// </summary>
[HarmonyPatch(typeof(FireboxSimController), nameof(FireboxSimController.TransferCoal))]
public static class FireboxTransferCoalPatch
{
    private static void Postfix(FireboxSimController __instance, float coalMass)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || !lifecycle.IsClientRunning || lifecycle.IsHost() || lifecycle.IsProcessingPacket)
            return;
        if (coalMass <= 0 || __instance.coalConsumptionMultiplier == 0)
            return;
        TrainCar car = __instance.GetComponentInParent<TrainCar>();
        if (car == null || !NetworkedTrainCar.TryGetFromTrainCar(car, out NetworkedTrainCar networkedCar))
            return;
        float delta = coalMass / __instance.coalConsumptionMultiplier;
        lifecycle.Client.LogDebug(() => $"FireboxTransferCoalPatch({car.ID}): coal added {delta}");
        lifecycle.Client.SendAddCoal(networkedCar.NetId, delta);
    }
}
