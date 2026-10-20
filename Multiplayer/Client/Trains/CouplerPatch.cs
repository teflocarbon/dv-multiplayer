using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.Trains;


[HarmonyPatch(typeof(Coupler))]
public static class CouplerPatch
{
    // Vanilla calls these on every couple, including when nothing changes (the hose is already
    // connected, or there was none to disconnect); only report real changes, or the host gets
    // commands it rejects (and, for a client, rolls back).
    [HarmonyPatch(nameof(Coupler.ConnectAirHose))]
    [HarmonyPrefix]
    private static void BeforeConnectAirHose(Coupler __instance, out Coupler __state) =>
        __state = __instance?.GetAirHoseConnectedTo();

    [HarmonyPatch(nameof(Coupler.ConnectAirHose))]
    [HarmonyPostfix]
    private static void ConnectAirHose(Coupler __instance, Coupler other, bool playAudio, Coupler __state)
    {
        if (__instance == null || __instance.GetAirHoseConnectedTo() == __state)
            return;
        //Multiplayer.LogDebug(() => $"ConnectAirHose([{__instance?.train?.ID}, isFront: {__instance?.isFrontCoupler}])\r\n{new System.Diagnostics.StackTrace()}");

        if (UnloadWatcher.isUnloading || NetworkLifecycle.Instance.IsProcessingPacket ||
            NetworkedTrainCar.IsApplyingAuthoritativeTopology ||
            NetworkedTrainCar.IsBatchingLocalTopology)
            return;

        //Ensure local car has initialised and brakes have been connected on spawn before sending any packets
        if (!NetworkedTrainCar.TryGetFromTrainCar(__instance?.train, out var netTrainCar) || !(netTrainCar?.Client_Initialized ?? false) || netTrainCar?.gameObject == null)
        {
            Multiplayer.LogWarning($"ConnectAirHose({__instance?.train?.ID}) netTrainCar found: {netTrainCar != null}, GO exists: {netTrainCar?.gameObject != null}, Initialised: {netTrainCar?.Client_Initialized}");
            return;
        }

        NetworkClient.Instance?.SendHoseConnected(__instance, other, playAudio);
    }

    [HarmonyPatch(nameof(Coupler.DisconnectAirHose))]
    [HarmonyPrefix]
    private static void BeforeDisconnectAirHose(Coupler __instance, out bool __state) =>
        __state = __instance?.GetAirHoseConnectedTo() != null;

    [HarmonyPatch(nameof(Coupler.DisconnectAirHose))]
    [HarmonyPostfix]
    private static void DisconnectAirHose(Coupler __instance, bool playAudio, bool __state)
    {
        if (!__state || __instance == null || __instance.GetAirHoseConnectedTo() != null)
            return;
        //Multiplayer.LogDebug(() => $"DisconnectAirHose([{__instance?.train?.ID}, isFront: {__instance?.isFrontCoupler}])\r\n{new System.Diagnostics.StackTrace()}");
        if (UnloadWatcher.isUnloading || NetworkLifecycle.Instance.IsProcessingPacket ||
            NetworkedTrainCar.IsApplyingAuthoritativeTopology ||
            NetworkedTrainCar.IsBatchingLocalTopology)
            return;

        //Ensure local car has initialised and brakes have been connected on spawn before sending any packets
        if (!NetworkedTrainCar.TryGetFromTrainCar(__instance?.train, out var netTrainCar) || !(netTrainCar?.Client_Initialized ?? false) || netTrainCar?.gameObject == null)
        {
            Multiplayer.LogWarning($"DisconnectAirHose({__instance?.train?.ID}) netTrainCar found: {netTrainCar != null}, GO exists: {netTrainCar?.gameObject != null},  Initialised: {netTrainCar?.Client_Initialized}");
            return;
        }

        Multiplayer.LogDebug(() => $"DisconnectAirHose({__instance?.train?.ID}, {__instance.isFrontCoupler})");
        NetworkClient.Instance?.SendHoseDisconnected(__instance, playAudio); 
    }

}
