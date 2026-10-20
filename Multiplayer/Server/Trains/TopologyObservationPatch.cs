using DV.HUD;
using DV.MultipleUnit;
using DV.Simulation.Brake;
using HarmonyLib;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;

namespace Multiplayer.Server.Trains;

/// <summary>
///     Hoses, cocks and MU cables changing in the server's world: by the host's player or by vanilla
///     (a couple connects the hoses). Unlike couples and uncouples (<see cref="NetworkedTrainCarServer"/>,
///     from the couplers' events) vanilla raises no event for them, so they're observed here and go
///     out with the next topology snapshot. The host's own client used to turn each into a topology
///     command, which the server answered with this same commit; a host with no client sends none
///     (docs/design/host-local-session.md, stage 3). An unchanged topology commits nothing.
/// </summary>
internal static class TopologyObservation
{
    // Not while the server applies another player's change: that commits once it's applied.
    internal static bool Observing =>
        NetworkRole.IsServer && !UnloadWatcher.isUnloading &&
        !NetworkLifecycle.Instance.IsProcessingPacket &&
        !NetworkedTrainCar.IsApplyingAuthoritativeTopology;

    internal static void Commit(TrainCar car)
    {
        if (Observing && car != null && car.TryNetworked(out NetworkedTrainCar networked) &&
            networked != null && !networked.IsDestroying)
            NetworkServer.Instance?.CommitObservedTrainTopology();
    }
}

[HarmonyPatch(typeof(Coupler))]
public static class CouplerHoseObservationPatch
{
    [HarmonyPatch(nameof(Coupler.ConnectAirHose))]
    [HarmonyPrefix]
    private static void BeforeConnectAirHose(Coupler __instance, out Coupler __state) =>
        __state = __instance?.GetAirHoseConnectedTo();

    [HarmonyPatch(nameof(Coupler.ConnectAirHose))]
    [HarmonyPostfix]
    private static void ConnectAirHose(Coupler __instance, Coupler __state)
    {
        if (__instance != null && __instance.GetAirHoseConnectedTo() != __state)
            TopologyObservation.Commit(__instance.train);
    }

    [HarmonyPatch(nameof(Coupler.DisconnectAirHose))]
    [HarmonyPrefix]
    private static void BeforeDisconnectAirHose(Coupler __instance, out bool __state) =>
        __state = __instance?.GetAirHoseConnectedTo() != null;

    [HarmonyPatch(nameof(Coupler.DisconnectAirHose))]
    [HarmonyPostfix]
    private static void DisconnectAirHose(Coupler __instance, bool __state)
    {
        if (__state && __instance != null && __instance.GetAirHoseConnectedTo() == null)
            TopologyObservation.Commit(__instance.train);
    }
}

[HarmonyPatch(typeof(HoseAndCock), nameof(HoseAndCock.SetCock))]
public static class CockObservationPatch
{
    private static void Prefix(HoseAndCock __instance, out bool __state) => __state = __instance.cockOpen;

    private static void Postfix(HoseAndCock __instance, bool __state)
    {
        if (__state != __instance.cockOpen && NetworkedTrainCar.TryGetCoupler(__instance, out Coupler coupler))
            TopologyObservation.Commit(coupler.train);
    }
}

[HarmonyPatch(typeof(MultipleUnitCable), nameof(MultipleUnitCable.Connect))]
public static class MultipleUnitConnectObservationPatch
{
    private static void Postfix(MultipleUnitCable __instance) =>
        TopologyObservation.Commit(__instance?.muModule?.train);
}

[HarmonyPatch(typeof(MultipleUnitCable), nameof(MultipleUnitCable.Disconnect))]
public static class MultipleUnitDisconnectObservationPatch
{
    private static void Postfix(MultipleUnitCable __instance) =>
        TopologyObservation.Commit(__instance?.muModule?.train);
}

// The coupling menu couples, connects and tightens in one press, and puts right a chain vanilla
// leaves loose or attached (Client/Trains/UICouplingHelperPatch): commit what it ends with.
[HarmonyPatch(typeof(UICouplingHelper), nameof(UICouplingHelper.HandleCoupling))]
public static class CouplingMenuObservationPatch
{
    private static void Postfix(Coupler coupler) => TopologyObservation.Commit(coupler?.train);
}
