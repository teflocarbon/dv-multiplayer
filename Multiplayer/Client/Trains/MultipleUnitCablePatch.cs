using DV.MultipleUnit;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.Trains;

[HarmonyPatch(typeof(MultipleUnitCable), nameof(MultipleUnitCable.Connect))]
public static class MultipleUnitCable_Connect_Patch
{
    private static void Postfix(MultipleUnitCable __instance, MultipleUnitCable other, bool playAudio)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket || UnloadWatcher.isUnloading ||
            NetworkedTrainCar.IsApplyingAuthoritativeTopology ||
            NetworkedTrainCar.IsBatchingLocalTopology)
            return;
        if (NetworkRole.IsRemoteClient) // the server observes its own world's (TopologyObservationPatch)
            NetworkClient.Instance?.SendMuConnected(__instance, other, playAudio);
    }
}

[HarmonyPatch(typeof(MultipleUnitCable), nameof(MultipleUnitCable.Disconnect))]
public static class MultipleUnitCable_Disconnect_Patch
{
    private static void Postfix(MultipleUnitCable __instance, bool playAudio)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket || UnloadWatcher.isUnloading ||
            NetworkedTrainCar.IsApplyingAuthoritativeTopology ||
            NetworkedTrainCar.IsBatchingLocalTopology)
            return;

        if (!__instance.muModule.train.TryNetworked(out NetworkedTrainCar networkedTrainCar) ||
            networkedTrainCar == null || networkedTrainCar.IsDestroying)
            return;
        if (NetworkRole.IsRemoteClient) // the server observes its own world's (TopologyObservationPatch)
            NetworkClient.Instance?.SendMuDisconnected(networkedTrainCar.NetId, __instance, playAudio);
    }
}
