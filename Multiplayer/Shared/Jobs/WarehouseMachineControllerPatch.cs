using HarmonyLib;

namespace Multiplayer.Shared.Jobs;

/// <summary>
/// Every game: each warehouse machine is networked. The server broadcasts its screen
/// (Server/Jobs/WarehouseMachineControllerServerPatch); a remote client asks the server to load and
/// unload (Client/Jobs/WarehouseMachineControllerClientPatch).
/// </summary>
[HarmonyPatch(typeof(WarehouseMachineController))]
public class WarehouseMachineControllerPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(WarehouseMachineController.Awake))]
    public static void Awake(WarehouseMachineController __instance)
    {
        __instance.gameObject.AddComponent<NetworkedWarehouseMachineController>();
    }
}
