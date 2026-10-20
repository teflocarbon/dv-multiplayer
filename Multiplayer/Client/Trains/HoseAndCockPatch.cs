using DV.Simulation.Brake;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.Trains;

[HarmonyPatch(typeof(HoseAndCock), nameof(HoseAndCock.SetCock))]
public static class HoseAndCock_SetCock_Patch
{
    private static void Prefix(HoseAndCock __instance, out bool __state) => __state = __instance.cockOpen;

    private static void Postfix(HoseAndCock __instance, bool open, bool __state)
    {
        // Only real changes: vanilla sets cocks it doesn't change (e.g. on every couple).
        if (__state == __instance.cockOpen)
            return;
        if (UnloadWatcher.isUnloading || NetworkLifecycle.Instance.IsProcessingPacket ||
            NetworkedTrainCar.IsApplyingAuthoritativeTopology ||
            NetworkedTrainCar.IsBatchingLocalTopology)
            return;

        if (!NetworkedTrainCar.TryGetCoupler(__instance, out Coupler coupler) || !coupler.train.TryNetworked(out NetworkedTrainCar networkedTrainCar))
            return;

        if (networkedTrainCar.IsDestroying)
            return;

        NetworkClient.Instance?.SendCockState(networkedTrainCar.NetId, coupler, open);
    }
}
