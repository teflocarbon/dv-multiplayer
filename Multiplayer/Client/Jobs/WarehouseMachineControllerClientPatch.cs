using HarmonyLib;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Jobs;

/// <summary>
/// A remote client's Load and Unload buttons ask the server to run the sequence.
/// </summary>
[HarmonyPatch(typeof(WarehouseMachineController))]
public class WarehouseMachineControllerClientPatch
{
    [HarmonyPrefix]
    [HarmonyPatch("StartUnloadSequence")]
    public static bool StartUnloadSequence_Prefix(WarehouseMachineController __instance)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return true;

        SendValidationRequest(__instance, WarehouseAction.Unload);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch("StartLoadSequence")]
    public static bool StartLoadSequence_Prefix(WarehouseMachineController __instance)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return true;

        SendValidationRequest(__instance, WarehouseAction.Load);
        return false;
    }

    private static void SendValidationRequest(WarehouseMachineController machine, WarehouseAction action)
    {
        string id = machine?.warehouseMachine?.ID;

        if (string.IsNullOrEmpty(id))
        {
            NetworkLifecycle.Instance.Client.LogError($"Failed to validate {action} for {machine?.name} at {machine?.warehouseTrackName}. Warehouse not found!");
            return;
        }

        if (!NetworkedWarehouseMachineController.GetFromWarehouseMachineController(machine, out var netController) || netController == null)
        {
            NetworkLifecycle.Instance.Client.LogError($"Failed to find NetworkedWarehouseMachineController {machine?.warehouseTrackName}. Warehouse not found!");
            return;
        }

        NetworkLifecycle.Instance.Client.SendWarehouseRequest(action, netController.NetId);
    }
}
