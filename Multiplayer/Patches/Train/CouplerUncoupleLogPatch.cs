using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;

namespace Multiplayer.Patches.Train;

/// <summary>
/// Logs every coupling the host takes apart, with who or what did it, so a train that comes
/// apart on its own can be traced from the host's log. The server names the player around
/// their topology commands; anything else is logged with the methods that called Uncouple.
/// Breaks are left to <see cref="CouplerBreakPatch"/>, which logs them as errors.
/// </summary>
[HarmonyPatch(typeof(Coupler), nameof(Coupler.Uncouple))]
public static class CouplerUncoupleLogPatch
{
    private const int CallerFrames = 6;

    private static string cause;

    /// <summary>Attributes the uncouples inside the scope to <paramref name="description"/>.</summary>
    internal static IDisposable Cause(string description)
    {
        string previous = cause;
        cause = description;
        return new CauseScope(previous);
    }

    [HarmonyPrefix]
    private static void Prefix(Coupler __instance, bool calledOnOtherCoupler, bool dueToBrokenCouple)
    {
        if (calledOnOtherCoupler || dueToBrokenCouple || __instance == null || !__instance.IsCoupled())
            return;
        if (NetworkLifecycle.Instance == null || !NetworkLifecycle.Instance.IsHost())
            return;

        try
        {
            Coupler other = __instance.coupledTo;
            TrainCar car = __instance.train, otherCar = other != null ? other.train : null;
            string by = cause ?? (NetworkLifecycle.Instance.IsProcessingPacket
                ? "a packet handler, " + Callers()
                : Callers());
            Multiplayer.Log(
                $"Uncoupled {car?.ID} ({End(__instance)}) from {otherCar?.ID} ({End(other)}) on the host, by {by}. " +
                $"Speeds {Speed(car):F2} and {Speed(otherCar):F2} m/s, derailed {car?.derailed}/{otherCar?.derailed}, " +
                $"chain {__instance.ChainScript?.state}/{other?.ChainScript?.state}");
        }
        catch (Exception e)
        {
            Multiplayer.LogException("Failed to log an uncouple", e);
        }
    }

    private static string Callers()
    {
        var names = new List<string>();
        foreach (StackFrame frame in new StackTrace(2, false).GetFrames() ?? Array.Empty<StackFrame>())
        {
            MethodBase method = frame.GetMethod();
            if (method == null || method.DeclaringType == typeof(CouplerUncoupleLogPatch))
                continue;
            names.Add(method.DeclaringType != null ? $"{method.DeclaringType.Name}.{method.Name}" : method.Name);
            if (names.Count == CallerFrames)
                break;
        }
        return names.Count > 0 ? string.Join(" < ", names) : "an unknown caller";
    }

    private static float Speed(TrainCar car) =>
        car != null && car.rb != null ? car.rb.velocity.magnitude : 0f;

    private static string End(Coupler coupler) =>
        coupler == null ? "none" : coupler.isFrontCoupler ? "Front" : "Rear";

    private sealed class CauseScope : IDisposable
    {
        private readonly string previous;
        private bool disposed;

        public CauseScope(string previous) => this.previous = previous;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            cause = previous;
        }
    }
}
