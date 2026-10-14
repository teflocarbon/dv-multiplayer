using System;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;

namespace Multiplayer.Patches.Train;

/// <summary>
/// Clients run their own coupler joints, so a hitch (a frame drop, a correction snapping a car)
/// can break a joint on one client only. Vanilla then uncouples locally and drops the air hose,
/// and the hose patch reported that to the host, which disconnected the hose between cars that
/// were still coupled: the brake pipe vented, the train went into emergency and drained the main
/// reservoir. Couplings break on the host, which owns the topology; on clients joints don't break,
/// and a local break that still happens is undone from the host's topology instead of reported.
/// </summary>
[HarmonyPatch(typeof(Coupler))]
public static class CouplerBreakPatch
{
    private static bool IsClient =>
        NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.Client != null &&
        !NetworkLifecycle.Instance.IsHost();

    [HarmonyPatch("CreateRigidJoint")]
    [HarmonyPostfix]
    private static void CreateRigidJoint(Coupler __instance)
    {
        if (!IsClient || __instance.rigidCJ == null)
            return;
        __instance.rigidCJ.breakForce = float.PositiveInfinity;
        __instance.rigidCJ.breakTorque = float.PositiveInfinity;
    }

    [HarmonyPatch(nameof(Coupler.Uncouple))]
    [HarmonyPrefix]
    private static void BeforeUncouple(Coupler __instance, bool calledOnOtherCoupler, bool dueToBrokenCouple, out IDisposable __state)
    {
        __state = null;
        if (!dueToBrokenCouple || calledOnOtherCoupler || !IsClient || !__instance.IsCoupled() ||
            NetworkLifecycle.Instance.IsProcessingPacket || NetworkedTrainCar.IsApplyingAuthoritativeTopology)
            return;
        Multiplayer.LogWarning($"Coupling at {__instance.train?.ID} broke locally; restoring it from the host");
        __state = NetworkedTrainCar.BeginLocalTopologyBatch();
    }

    [HarmonyPatch(nameof(Coupler.Uncouple))]
    [HarmonyPostfix]
    private static void AfterUncouple(IDisposable __state)
    {
        if (__state == null)
            return;
        __state.Dispose();
        NetworkLifecycle.Instance.Client?.RestoreCanonicalTrainTopology();
    }

    [HarmonyPatch(nameof(Coupler.Uncouple))]
    [HarmonyFinalizer]
    private static Exception UncoupleFinalizer(Exception __exception, IDisposable __state)
    {
        __state?.Dispose();
        return __exception;
    }
}
