using DV.MultipleUnit;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Utils;

namespace Multiplayer.Patches.World;

[HarmonyPatch(typeof(MultipleUnitCable), nameof(MultipleUnitCable.Connect))]
public static class MultipleUnitCable_Connect_Patch
{
    private static void Postfix(MultipleUnitCable __instance, MultipleUnitCable other, bool playAudio)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket || UnloadWatcher.isUnloading ||
            NetworkedTrainCar.IsApplyingAuthoritativeTopology ||
            NetworkedTrainCar.IsBatchingLocalTopology)
            return;
        NetworkLifecycle.Instance.Client?.SendMuConnected(__instance, other, playAudio);
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
        NetworkLifecycle.Instance.Client?.SendMuDisconnected(networkedTrainCar.NetId, __instance, playAudio);
    }
}
