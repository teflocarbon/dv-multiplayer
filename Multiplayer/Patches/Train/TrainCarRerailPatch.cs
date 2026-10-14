using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Patches.Train;

/// <summary>
/// Vanilla only lets a derailed car be rerailed once it is moving slower than 1 m/s. A car that
/// glitched into the ground jitters, or falls through it, and never gets there, so it could never
/// be recovered (on clients the speed is the host's, so nobody could). Once a car has been derailed
/// for a while, whatever it is still doing is a glitch: allow the rerail, which zeroes its velocity.
/// </summary>
[HarmonyPatch(typeof(TrainCar))]
public static class TrainCarRerailPatch
{
    private const float StuckAfterSeconds = 30f;

    private sealed class DerailTime { public float At; }
    private static readonly ConditionalWeakTable<TrainCar, DerailTime> derailedAt = new();

    [HarmonyPatch(nameof(TrainCar.Derail))]
    [HarmonyPostfix]
    private static void Derail(TrainCar __instance)
    {
        derailedAt.Remove(__instance);
        derailedAt.Add(__instance, new DerailTime { At = Time.time });
    }

    [HarmonyPatch(nameof(TrainCar.IsRerailAllowed), MethodType.Getter)]
    [HarmonyPostfix]
    private static void IsRerailAllowed(TrainCar __instance, ref bool __result)
    {
        if (__result || !__instance.derailed || __instance.preventRerail)
            return;
        // A car derailed before this session (loaded derailed, or derailed on the host before we
        // joined) counts from the first time anyone asks.
        DerailTime since = derailedAt.GetValue(__instance, _ => new DerailTime { At = Time.time });
        if (Time.time - since.At >= StuckAfterSeconds)
            __result = true;
    }
}
