using DV;
using DV.Simulation.Controllers;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using UnityEngine;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.Trains;

/// <summary>
/// Coal a player takes from a tender's coal pile, or puts back, with a shovel. Both go through
/// CoalPileSimController, which a client reports to the host (it takes the same coal there).
///
/// This used to watch the coal pile's consume port instead, which a mechanical stoker writes too
/// (Custom Car Loader's SteamMechanicalStoker, every simulation tick on every game). Every game
/// reported its own stoker's coal, and the host took each report on top of its own stoker; the
/// host's own client sent itself two reliable packets a tick per stoker tender, which the server
/// then ignored (playtest 2: 340,000 debug lines in one night, before the server's GC crash).
/// The host never reports: it applies its own coal.
/// </summary>
[HarmonyPatch(typeof(CoalPileSimController))]
public static class CoalPileTransferCoalPatch
{
    [HarmonyPatch(nameof(CoalPileSimController.TryRemoveCoal))]
    [HarmonyPostfix]
    private static void TryRemoveCoal(CoalPileSimController __instance, float __result) =>
        Report(__instance, __result);

    [HarmonyPatch(nameof(CoalPileSimController.TryAddCoal))]
    [HarmonyPostfix]
    private static void TryAddCoal(CoalPileSimController __instance, float __result) =>
        Report(__instance, -__result);

    /// <param name="coalTaken">Coal taken from the pile, negative for coal added: the sign the
    /// consume port takes, before the game's resource consumption modifier.</param>
    private static void Report(CoalPileSimController coalPile, float coalTaken)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || !NetworkRole.IsRemoteClient || lifecycle.IsProcessingPacket)
            return;
        // An empty pile or a full one moves nothing.
        if (!(Mathf.Abs(coalTaken) > 0f))
            return;
        TrainCar car = coalPile.GetComponentInParent<TrainCar>();
        if (car == null || !NetworkedTrainCar.TryGetFromTrainCar(car, out NetworkedTrainCar networkedCar))
            return;
        // The host passes this straight to the consume port, as the game does after scaling it.
        NetworkClient.Instance.SendTenderCoalPileInteraction(networkedCar.NetId,
            coalTaken * Globals.G.GameParams.ResourceConsumptionModifier);
    }
}
