using DV.Logic.Job;
using DV.ThingTypes;
using HarmonyLib;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using static WarehouseMachineController;

namespace Multiplayer.Server.Jobs;

/// <summary>
/// The server runs the warehouse machines and broadcasts what their screens say. The idle and
/// train-in-range screens are left to each game.
/// </summary>
[HarmonyPatch(typeof(WarehouseMachineController))]
public class WarehouseMachineControllerServerPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(WarehouseMachineController.SetScreen))]
    public static void SetScreen(WarehouseMachineController __instance, TextPreset preset, bool isLoading, string jobId, Car car, CargoType_v2 cargoType)
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        bool skip = preset switch
        {
            TextPreset.Idle => true,
            TextPreset.TrainInRange => true,
            TextPreset.ClearTrainInRange => true,
            _ => false
        };

        if (skip)
            return;

        if (!NetworkedWarehouseMachineController.GetFromWarehouseMachineController(__instance, out var netMachine) || netMachine == null)
        {
            Multiplayer.LogError($"WarehouseMachineControllerServerPatch.SetScreen(): Failed to get NetworkedWarehouseMachineController for {__instance.warehouseTrackName}");
            return;
        }

        //obtain serialisable info
        ushort carNetId = 0;
        ushort jobNetId = 0;

        if (car != null)
        {
            var tc = car.TrainCar();
            if (tc == null || !NetworkedTrainCar.TryGetFromTrainCar(tc, out var netTC))
                return;

            carNetId = netTC.NetId;
        }

        if (!string.IsNullOrEmpty(jobId))
        {
            if(!NetworkedJob.TryGetFromJobId(jobId, out var netJob))
            {
                Multiplayer.LogWarning($"WarehouseMachineControllerServerPatch.SetScreen() Failed to get NetworkedJob for {jobId}");
                return;
            }

            jobNetId = netJob.NetId;
        }

        CargoTypeLookup.Instance.TryGetNetId(cargoType, out uint cargoTypeNetId);

        NetworkLifecycle.Instance.Server.SendWarehouseControllerUpdate(netMachine.NetId, isLoading, jobNetId, carNetId, cargoTypeNetId, preset);
    }
}
