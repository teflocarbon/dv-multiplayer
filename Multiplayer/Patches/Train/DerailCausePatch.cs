using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;
using Multiplayer.Debugging;
using UnityEngine;

namespace Multiplayer.Patches.Train;

/// <summary>
/// Records and logs why every car derails as a <see cref="DerailReason"/> cause
/// (NetworkedTrainCar.LastDerail), and the host sends it to clients. Vanilla only logs a loose
/// message, and nothing at all when a derail is called directly (the comms radio), so a derail
/// used to be traced by matching log timings. The message vanilla derails a bogie or a car's
/// stress with is caught on the way in to TrainCar.Derail.
/// </summary>
[HarmonyPatch]
public static class DerailCausePatch
{
    private static string stressMessage;
    private static string bogieMessage;

    [HarmonyPatch(typeof(TrainStress), nameof(TrainStress.Derail))]
    [HarmonyPrefix]
    private static void StressDerail(string msg) => stressMessage = msg ?? string.Empty;

    [HarmonyPatch(typeof(TrainStress), nameof(TrainStress.Derail))]
    [HarmonyFinalizer]
    private static Exception StressDerailDone(Exception __exception)
    {
        stressMessage = null;
        return __exception;
    }

    [HarmonyPatch(typeof(Bogie), nameof(Bogie.Derail))]
    [HarmonyPrefix]
    private static void BogieDerail(Bogie __instance, string message)
    {
        // The first bogie's message is the car's; the rest come from the car's own derail.
        if (!__instance.HasDerailed && bogieMessage == null)
            bogieMessage = message ?? string.Empty;
    }

    [HarmonyPatch(typeof(Bogie), nameof(Bogie.Derail))]
    [HarmonyFinalizer]
    private static Exception BogieDerailDone(Exception __exception)
    {
        bogieMessage = null;
        return __exception;
    }

    [HarmonyPatch(typeof(TrainCar), nameof(TrainCar.Derail))]
    [HarmonyPrefix]
    private static void CarDerail(TrainCar __instance)
    {
        if (!Application.isPlaying || __instance == null || __instance.derailed)
            return;
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || (lifecycle.Server == null && lifecycle.Client == null))
            return;

        try
        {
            ChangeCause<DerailReason> cause = Determine();
            bool host = lifecycle.IsHost();
            NetworkedTrainCar.TryGetFromTrainCar(__instance, out NetworkedTrainCar car);
            // A client's own derail only follows the host's; the host's cause arrives by packet.
            if (car != null && (host || cause.Reason != DerailReason.FollowingHost))
                car.LastDerail = cause;
            if (host && car != null)
                lifecycle.Server.SendDerailCause(car, cause);
            Multiplayer.Log($"{__instance.ID} derailed on the {(host ? "host" : "client")}: {cause}");
            DebugRuntime.Publish("train", "train.derailed", TrainChangeCauses.Side, entityType: "TrainCar",
                entityId: (car?.NetId ?? 0).ToString(), data: TrainChangeCauses.Data(cause, ("car", __instance.ID)));
        }
        catch (Exception e)
        {
            Multiplayer.LogException("Failed to record a derail", e);
        }
    }

    private static ChangeCause<DerailReason> Determine()
    {
        if (TrainChangeCauses.CurrentCause<DerailReason>() is ChangeCause<DerailReason> scoped)
            return scoped;
        if (stressMessage != null)
            return TrainChangeCauses.Cause(DerailReason.Stress, detail: stressMessage);
        switch (bogieMessage)
        {
            case "Explosion":
                return TrainChangeCauses.Cause(DerailReason.Explosion);
            case "Reached end of track!":
                return TrainChangeCauses.Cause(DerailReason.EndOfTrack);
            case "Due to missing track":
                return TrainChangeCauses.Cause(DerailReason.MissingTrack);
            case "Loaded state":
                return TrainChangeCauses.Cause(DerailReason.LoadedDerailed);
        }

        List<MethodBase> callers = TrainChangeCauses.Callers(2);
        if (TrainChangeCauses.Called(callers, nameof(SafetyDerailer)))
            return TrainChangeCauses.Cause(DerailReason.SafetyDerailer);
        // This peer's own player (a host playing): vanilla's radio derail mode.
        if (TrainChangeCauses.Called(callers, "CommsRadioDamage"))
            return TrainChangeCauses.LocalCause(DerailReason.CommsRadio);
        string detail = string.IsNullOrEmpty(bogieMessage)
            ? TrainChangeCauses.Describe(callers)
            : $"bogie: {bogieMessage}; {TrainChangeCauses.Describe(callers)}";
        return TrainChangeCauses.Cause(DerailReason.Unknown, detail: detail);
    }
}
