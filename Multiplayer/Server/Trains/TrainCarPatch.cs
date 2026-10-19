using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using Multiplayer.Shared.World;
using UnityEngine;
#if DEBUG
#endif

namespace Multiplayer.Server.Trains;

[HarmonyPatch(typeof(TrainCar))]
public static class TrainCarPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(TrainCar.Awake))]
    private static void Awake_Prefix(TrainCar __instance)
    {
        InitNetworkedTrainCar(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(TrainCar.AwakeForPooledCar))]
    private static void AwakeForPooledCar_Prefix(TrainCar __instance)
    {
        InitNetworkedTrainCar(__instance);
    }

    private static void InitNetworkedTrainCar(TrainCar __instance)
    {
        if (CarSpawner.Instance.PoolSetupInProgress)
            return;
        // A car taken from the pool in the frame it was returned still has its old, retired
        // component (see NetworkedTrainCar.OnDisable): replace it, or the new car loses its
        // network identity at the end of the frame.
        NetworkedTrainCar existing = __instance.GetComponent<NetworkedTrainCar>();
        if (existing != null && existing.Retired)
            Object.DestroyImmediate(existing);
        __instance.GetOrAddComponent<NetworkedTrainCar>();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(TrainCar.Rerail))]
    private static void Rerail_Prefix(TrainCar __instance, RailTrack rerailTrack, Vector3 worldPos, Vector3 forward)
    {
        if (!NetworkLifecycle.Instance.IsHost() || NetworkLifecycle.Instance.IsProcessingPacket)
            return;
        if (!__instance.derailed || !__instance.TryNetworked(out NetworkedTrainCar networkedTrainCar))
            return;
        NetworkLifecycle.Instance.Server.SendRerailTrainCar(networkedTrainCar.NetId, NetworkedRailTrack.GetFromRailTrack(rerailTrack).NetId, worldPos - WorldMover.currentMove, forward);
        networkedTrainCar.Server_ResyncAfterRelocation();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(TrainCar.MoveToTrackWithCarUncouple))]
    private static void MoveToTrackWithCarUncouple(TrainCar __instance, RailTrack destinationTrack, Vector3 worldPos, Vector3 forward)
    {
        Multiplayer.LogDebug(() => $"MoveToTrackWithCarUncouple({__instance?.ID}) isHost: {NetworkLifecycle.Instance.IsHost()}, isProcessingPacket: {NetworkLifecycle.Instance.IsProcessingPacket}, isDerailed: {__instance.derailed}, destinationTrack: {destinationTrack?.name}, worldPos: {worldPos}, forward: {forward}");

        if (!NetworkLifecycle.Instance.IsHost() || NetworkLifecycle.Instance.IsProcessingPacket)
            return;
        if (!__instance.TryNetworked(out NetworkedTrainCar networkedTrainCar))
            return;
        if (__instance.derailed)
            return;

        NetworkLifecycle.Instance.Server.SendMoveTrainCarToTrack(networkedTrainCar.NetId, NetworkedRailTrack.GetFromRailTrack(destinationTrack).NetId, worldPos - WorldMover.currentMove, forward, false);
        networkedTrainCar.Server_ResyncAfterRelocation();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(TrainCar.UpdateCouplerJoints))]
    private static bool UpdateCouplerJoints(TrainCar __instance)
    {
        return NetworkLifecycle.Instance.IsHost();
    }
}
