using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Jobs;
using Multiplayer.Shared.Trains;
using System.Collections.Generic;
using static WarehouseMachineController;

namespace Multiplayer.Client.Jobs;

/// <summary>
/// A client shows what the server's warehouse machine screens say
/// (<c>Server/Jobs/WarehouseMachineControllerServerPatch</c> sends them).
/// </summary>
public static class WarehouseMachineScreen
{
    public static void ProcessUpdate(NetworkedWarehouseMachineController controller, ClientboundWarehouseControllerUpdatePacket packet)
    {
        TextPreset preset = (TextPreset)packet.Preset;
        bool isLoading = packet.IsLoading;
        string jobId = null;
        Car car = null;
        CargoType_v2 cargoType_V2 = null;
        string extra = null;

        WarehouseMachineController machineController = controller.WarehouseMachineController;
        if (machineController == null)
            return;

        if (packet.CarNetId != 0)
        {
            if (!NetworkedTrainCar.TryGet(packet.CarNetId, out NetworkedTrainCar networkedCar))
            {
                Multiplayer.LogWarning($"NetworkedWarehouseMachineController failed to find TrainCar with NetId: {packet.NetId}");
                return;
            }

            car = networkedCar.TrainCar.logicCar;
        }

        if (packet.JobNetId != 0)
        {
            if (!NetworkedJob.Get(packet.JobNetId, out var networkedJob))
            {
                Multiplayer.LogWarning($"NetworkedWarehouseMachineController failed to find Job with NetId: {packet.JobNetId}");
                return;
            }

            jobId = networkedJob.Job.ID;
        }

        if (car != null && jobId != null)
        {
            if (!CargoTypeLookup.Instance.TryGet(packet.CargoTypeNetId, out cargoType_V2))
            {
                Multiplayer.LogWarning($"NetworkedWarehouseMachineController failed to find CargoType with netId: {packet.CargoTypeNetId} for JobId: {jobId} on Car: {car.ID}");
            }
        }

        machineController?.SetScreen(preset, isLoading, jobId, car, cargoType_V2, extra);

        //special case for car updated - remove task from machine
        if (preset == TextPreset.CarUpdated && controller.WarehouseMachine != null)
        {
            CleanupTask(controller.WarehouseMachine, isLoading);
        }

        //special case for clearing - play sound
        if (preset == TextPreset.ClearDesc)
            machineController?.machineSound?.Play(machineController.transform.position, 1f, 1f, 0f, 1f, 500f, default, null, controller.transform, false, 0f, null);
    }

    private static void CleanupTask(WarehouseMachine warehouseMachine, bool isLoading)
    {
        List<WarehouseMachine.WarehouseLoadUnloadDataPerJob> currentLoadUnloadData = warehouseMachine.GetCurrentLoadUnloadData(isLoading ? WarehouseTaskType.Loading : WarehouseTaskType.Unloading);

        foreach (var data in currentLoadUnloadData)
        {
            if (data.tasksAvailableToProcess == null)
                continue;

            foreach (var task in data.tasksAvailableToProcess)
                warehouseMachine.RemoveWarehouseTask(task);

        }
    }
}
