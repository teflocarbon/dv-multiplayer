using DV.Customization.Gadgets.Implementations;
using HarmonyLib;
using Multiplayer.Shared.Session;
using System;
using UnityEngine;

namespace Multiplayer.Shared.Gadgets;

/// <summary>
/// A beacon's spin and an oscillating light's sweep, the same in every game of a session
/// (gadgets.md, "Shared motion and randomness"): vanilla adds up local frame time, so two games show
/// different angles. In a session the angle comes from the shared tick clock and the gadget's UID
/// instead, and needs no traffic. A change of speed (its switch turned) moves the phase at once.
/// </summary>
[HarmonyPatch]
internal static class GadgetSharedMotionPatches
{
    private static uint seenTick;
    private static float seenAt;

    /// <summary>The session's time in seconds, the same in every game to a tick: the shared tick
    /// count, and the time since this game last saw it advance.</summary>
    private static double SessionSeconds()
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        float now = Time.realtimeSinceStartup;
        if (lifecycle.Tick != seenTick)
        {
            seenTick = lifecycle.Tick;
            seenAt = now;
        }
        return (double)lifecycle.Tick / NetworkLifecycle.TICK_RATE +
               Math.Min(now - seenAt, 1f / NetworkLifecycle.TICK_RATE);
    }

    private static bool InSession => NetworkLifecycle.Instance is { IsSessionRunning: true };

    // A different start for each gadget, so beacons side by side don't turn in step.
    private static double Offset(int uid) => (uid * 0.618034) % 1.0;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetBeacon), "Update")]
    private static bool BeaconUpdate(GadgetBeacon __instance)
    {
        if (!InSession || __instance.head == null)
            return true;
        if (__instance.rotateSpeed != 0f)
        {
            double turns = __instance.rotateSpeed / 360.0 * SessionSeconds() + Offset(__instance.UID);
            __instance.angle = (float)((turns - Math.Floor(turns)) * 360.0);
            __instance.head.localRotation = Quaternion.AngleAxis(__instance.angle, Vector3.up);
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetOscillatingLight), "Update")]
    private static bool OscillatingLightUpdate(GadgetOscillatingLight __instance)
    {
        if (!InSession || __instance.head == null)
            return true;
        if (__instance.currentSpeed != 0f)
        {
            double cycles = __instance.currentSpeed * SessionSeconds() + Offset(__instance.UID);
            __instance.time = (float)(cycles - Math.Floor(cycles));
            float phase = __instance.time * 2f * Mathf.PI;
            __instance.head.localRotation = Quaternion.Euler(Mathf.Sin(phase * 2f) * __instance.amplitude.x,
                Mathf.Sin(phase) * __instance.amplitude.y, 0f);
        }
        return false;
    }
}
