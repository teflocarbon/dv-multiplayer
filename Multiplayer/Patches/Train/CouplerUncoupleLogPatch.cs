using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;
using Multiplayer.Debugging;

namespace Multiplayer.Patches.Train;

/// <summary>
/// Records and logs why every coupling comes apart, on the host and on clients, as an
/// <see cref="UncoupleReason"/> cause kept on both cars' ends (NetworkedTrainCar.LastFrontUncouple
/// and LastRearUncouple). The host sends its causes with the next topology snapshot, so a client
/// following the host records the host's reason rather than just FollowingHost.
/// </summary>
[HarmonyPatch(typeof(Coupler), nameof(Coupler.Uncouple))]
public static class CouplerUncoupleLogPatch
{
    [HarmonyPrefix]
    private static void Prefix(Coupler __instance, bool calledOnOtherCoupler, bool dueToBrokenCouple)
    {
        if (calledOnOtherCoupler || __instance == null || !__instance.IsCoupled())
            return;
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || (lifecycle.Server == null && lifecycle.Client == null))
            return;

        try
        {
            Coupler other = __instance.coupledTo;
            TrainCar car = __instance.train, otherCar = other != null ? other.train : null;
            ChangeCause<UncoupleReason> cause = Determine(__instance, other, dueToBrokenCouple);
            TrainChangeCauses.RecordUncouple(__instance, cause);
            TrainChangeCauses.RecordUncouple(other, cause);

            bool host = lifecycle.IsHost();
            if (host)
                lifecycle.Server.NoteUncouple(__instance, cause);
            Multiplayer.Log($"Uncoupled {car?.ID} ({End(__instance)}) from {otherCar?.ID} ({End(other)}) " +
                $"on the {(host ? "host" : "client")}: {cause}");
            DebugRuntime.Publish("train", "train.uncoupled", TrainChangeCauses.Side, entityType: "TrainCar",
                entityId: NetId(car).ToString(), data: TrainChangeCauses.Data(cause,
                    ("car", car?.ID), ("end", End(__instance)), ("otherCar", otherCar?.ID),
                    ("otherEnd", End(other))));
        }
        catch (Exception e)
        {
            Multiplayer.LogException("Failed to record an uncouple", e);
        }
    }

    private static ChangeCause<UncoupleReason> Determine(Coupler coupler, Coupler other, bool dueToBrokenCouple)
    {
        if (dueToBrokenCouple)
        {
            bool derailed = coupler.train != null && coupler.train.derailed ||
                            other != null && other.train != null && other.train.derailed;
            return TrainChangeCauses.Cause(derailed ? UncoupleReason.JointBrokeDerailed : UncoupleReason.JointBroke);
        }

        if (TrainChangeCauses.CurrentCause<UncoupleReason>() is ChangeCause<UncoupleReason> scoped)
            return scoped;

        // A client applying the host's topology: the host's own reason if it sent one.
        if (NetworkedTrainCar.IsApplyingAuthoritativeTopology && !NetworkLifecycle.Instance.IsHost() &&
            NetworkLifecycle.Instance.Client is { } client)
        {
            if (client.RefusalUncoupleCause is ChangeCause<UncoupleReason> refused)
                return refused;
            if (client.TakeHostUncoupleCause(coupler, other, out ChangeCause<UncoupleReason> hostCause))
                return hostCause;
            return TrainChangeCauses.Cause(UncoupleReason.FollowingHost);
        }

        List<MethodBase> callers = TrainChangeCauses.Callers(2);
        if (TrainChangeCauses.Called(callers, nameof(CarSpawner), "PrepareTrainCarForDeleting"))
            return TrainChangeCauses.Cause(UncoupleReason.CarRemoved,
                detail: TrainChangeCauses.CurrentCause<CarRemovalReason>()?.ToString());
        if (TrainChangeCauses.Called(callers, nameof(TrainCar), nameof(TrainCar.MoveToTrackWithCarUncouple)) ||
            TrainChangeCauses.Called(callers, nameof(TrainCar), nameof(TrainCar.Rerail)))
            return TrainChangeCauses.LocalCause(UncoupleReason.CarRerailed);
        // This peer's own player, through vanilla's interactions (a host playing, mostly).
        if (TrainChangeCauses.Called(callers, "ChainCouplerInteraction") ||
            TrainChangeCauses.Called(callers, "UICouplingHelper") ||
            TrainChangeCauses.Called(callers, "CouplingHelper"))
            return TrainChangeCauses.LocalCause(UncoupleReason.PlayerCommand);
        if (TrainChangeCauses.Called(callers, "DecouplerDeviceLogic") ||
            TrainChangeCauses.Called(callers, "RemoteControllerModule"))
            return TrainChangeCauses.LocalCause(UncoupleReason.Remote);
        return TrainChangeCauses.Cause(UncoupleReason.Unknown, detail: TrainChangeCauses.Describe(callers));
    }

    private static ushort NetId(TrainCar car) =>
        car != null && NetworkedTrainCar.TryGetFromTrainCar(car, out NetworkedTrainCar networked) ? networked.NetId : (ushort)0;

    private static string End(Coupler coupler) =>
        coupler == null ? "none" : coupler.isFrontCoupler ? "Front" : "Rear";
}
