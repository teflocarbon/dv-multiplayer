using System;
using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Core.Trains;
using Multiplayer.Debugging.Protocol;
using UnityEngine;

namespace Multiplayer.Client.Trains;

/// <summary>
/// Coupler joints break at vanilla's 300 MN (about a hundred times a real coupler), so normal
/// play never breaks one: a break means the physics has gone badly wrong somewhere, and every
/// break is logged as an error with what led up to it.
///
/// Couplings belong to the host, which owns the topology. A joint that breaks on a client only
/// (two cars the host has apart being pulled apart by their corrections, say) isn't reported to
/// the host: the hose patch would disconnect the hose between cars the host still has coupled,
/// venting the brake pipe. The client restores the host's topology instead. Client joints used
/// to be unbreakable for that reason, but a joint that can't give lets a desync pull two cars
/// apart until the solver blows up (#64).
/// </summary>
[HarmonyPatch(typeof(Coupler))]
public static class CouplerBreakPatch
{
    private const float VanillaBreakForce = 300_000_000f;

    // The force a joint broke at, from CouplerBreakDetector.OnJointBreak, for the uncouple a frame
    // later. Both of a car's detectors hear every break on it, so the coupler that didn't break
    // gets a record too: records are only used within a few frames.
    private static readonly Dictionary<Coupler, (int frame, float force)> breakForces = new();

    private static bool IsClient =>
        NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.Client != null &&
        !NetworkLifecycle.Instance.IsHost();

    internal static void RecordJointBreak(Coupler coupler, float force)
    {
        if (coupler == null)
            return;
        if (breakForces.Count > 32)
            breakForces.Clear(); // the records nothing uncoupled for
        breakForces[coupler] = (Time.frameCount, force);
    }

    [HarmonyPatch(nameof(Coupler.Uncouple))]
    [HarmonyPrefix]
    private static void BeforeUncouple(Coupler __instance, bool calledOnOtherCoupler, bool dueToBrokenCouple, out IDisposable __state)
    {
        __state = null;
        if (!dueToBrokenCouple || calledOnOtherCoupler || !__instance.IsCoupled())
            return;
        LogBreak(__instance);
        if (!IsClient || NetworkLifecycle.Instance.IsProcessingPacket || NetworkedTrainCar.IsApplyingAuthoritativeTopology)
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

    private static void LogBreak(Coupler coupler)
    {
        try
        {
            Coupler other = coupler.coupledTo;
            TrainCar car = coupler.train, otherCar = other != null ? other.train : null;
            float force = breakForces.TryGetValue(coupler, out var record) && Time.frameCount - record.frame <= 5
                ? record.force
                : float.NaN;
            breakForces.Remove(coupler);
            ushort netId = NetId(car), otherNetId = NetId(otherCar);
            Vector3 velocity = Velocity(car), otherVelocity = Velocity(otherCar);
            float gap = other != null ? Vector3.Distance(coupler.transform.position, other.transform.position) : float.NaN;
            bool client = IsClient;
            string host = client
                ? NetworkLifecycle.Instance.Client.DescribeHostCoupling(netId, End(coupler))
                : "(this is the host)";

            Multiplayer.LogError(
                $"Coupler broke between {car?.ID} ({End(coupler)}) and {otherCar?.ID} ({End(other)}) on the " +
                $"{(client ? "client" : "host")}, which normal play never does: something has gone badly wrong. " +
                $"Joint force {force / 1e6f:F0} MN (breaks at {VanillaBreakForce / 1e6f:F0} MN), speeds " +
                $"{velocity.magnitude:F2} and {otherVelocity.magnitude:F2} m/s (relative " +
                $"{(velocity - otherVelocity).magnitude:F2}), couplers {gap:F2} m apart, derailed " +
                $"{car?.derailed}/{otherCar?.derailed}, host has this end {host}");

            DebugRuntime.Publish("train", "train.coupler-broken",
                client ? DebugRuntimeSide.Client : DebugRuntimeSide.Server, DebugSeverity.Error,
                "TrainCar", netId.ToString(), new Dictionary<string, object>
                {
                    ["car"] = car?.ID,
                    ["end"] = End(coupler).ToString(),
                    ["otherCar"] = otherCar?.ID,
                    ["otherNetId"] = otherNetId,
                    ["otherEnd"] = End(other).ToString(),
                    ["jointForce"] = force,
                    ["speed"] = velocity.magnitude,
                    ["otherSpeed"] = otherVelocity.magnitude,
                    ["relativeSpeed"] = (velocity - otherVelocity).magnitude,
                    ["couplerGap"] = gap,
                    ["derailed"] = car != null && car.derailed,
                    ["otherDerailed"] = otherCar != null && otherCar.derailed,
                    ["hostCoupling"] = host
                });
        }
        catch (Exception e)
        {
            Multiplayer.LogException("Failed to log a coupler break", e);
        }
    }

    private static ushort NetId(TrainCar car) =>
        car != null && NetworkedTrainCar.TryGetFromTrainCar(car, out NetworkedTrainCar networked) ? networked.NetId : (ushort)0;

    private static Vector3 Velocity(TrainCar car) =>
        car != null && car.rb != null ? car.rb.velocity : Vector3.zero;

    private static TrainEnd End(Coupler coupler) =>
        coupler != null && coupler.isFrontCoupler ? TrainEnd.Front : TrainEnd.Rear;
}

/// <summary>Unity tells every script on the joint's car; each detector keeps its own coupler.</summary>
[HarmonyPatch(typeof(CouplerBreakDetector), "OnJointBreak")]
public static class CouplerBreakDetectorPatch
{
    private static void Prefix(CouplerBreakDetector __instance, float breakForce) =>
        CouplerBreakPatch.RecordJointBreak(__instance.motherCoupler, breakForce);
}
