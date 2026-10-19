using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Managers.Client;

namespace Multiplayer.Components.Networking.Train;

/// <summary>
/// Records why train changes happened (uncouples, derails, car removals) as ChangeCauses. Code that
/// knows the cause (a player's command, a packet from the host) opens a scope with
/// <see cref="Scope{TReason}"/>; the patches that see the change read it, or infer the cause from
/// the vanilla methods that called it, or fall back to Unknown with those methods as the detail.
/// Everything runs on the main thread.
/// </summary>
internal static class TrainChangeCauses
{
    private const int CallerFrames = 6;
    private const int RecentRemovalCount = 64;

    private static readonly Queue<(string CarId, ushort NetId, ChangeCause<CarRemovalReason> Cause)> recentRemovals = new();

    /// <summary>The last cars removed on this peer and why, newest last (cars keep no record once gone).</summary>
    internal static IEnumerable<(string CarId, ushort NetId, ChangeCause<CarRemovalReason> Cause)> RecentRemovals => recentRemovals;

    internal static ChangeCause<TReason> Cause<TReason>(TReason reason, ServerPlayer player = null,
        string detail = null) where TReason : struct, Enum =>
        new(reason, player?.PlayerId ?? 0, player?.Username, Tick, detail);

    /// <summary>A cause acted by this peer's own player (a host playing, or vanilla code on a client).</summary>
    internal static ChangeCause<TReason> LocalCause<TReason>(TReason reason, string detail = null)
        where TReason : struct, Enum
    {
        NetworkClient client = NetworkLifecycle.Instance?.Client;
        return new ChangeCause<TReason>(reason, client?.PlayerId ?? 0, client?.Username, Tick, detail);
    }

    private static uint Tick => NetworkLifecycle.Instance != null ? NetworkLifecycle.Instance.Tick : 0;

    /// <summary>Attributes the changes inside the scope to <paramref name="cause"/>.</summary>
    internal static IDisposable Scope<TReason>(ChangeCause<TReason> cause) where TReason : struct, Enum =>
        Current<TReason>.Begin(cause);

    internal static ChangeCause<TReason>? CurrentCause<TReason>() where TReason : struct, Enum =>
        Current<TReason>.Value;

    /// <summary>The methods that called into the patched method, innermost first, past Harmony's frames.</summary>
    internal static List<MethodBase> Callers(int skipFrames)
    {
        var methods = new List<MethodBase>();
        foreach (StackFrame frame in new StackTrace(skipFrames + 1, false).GetFrames() ?? Array.Empty<StackFrame>())
        {
            // A patched method's frame is Harmony's replacement; map it back to the original.
            MethodBase method = HarmonyLib.Harmony.GetMethodFromStackframe(frame) ?? frame.GetMethod();
            if (method == null || method.DeclaringType?.Namespace?.StartsWith("Multiplayer.Patches") == true ||
                method.DeclaringType == typeof(TrainChangeCauses))
                continue;
            methods.Add(method);
        }
        return methods;
    }

    internal static bool Called(IEnumerable<MethodBase> callers, string typeName, string methodName = null) =>
        callers.Any(method => method.DeclaringType?.Name == typeName &&
                              (methodName == null || method.Name == methodName));

    internal static string Describe(IEnumerable<MethodBase> callers)
    {
        string[] names = callers.Take(CallerFrames)
            .Select(method => method.DeclaringType != null ? $"{method.DeclaringType.Name}.{method.Name}" : method.Name)
            .ToArray();
        return names.Length > 0 ? "called from " + string.Join(" < ", names) : "unknown caller";
    }

    internal static void RecordUncouple(Coupler coupler, ChangeCause<UncoupleReason> cause)
    {
        if (coupler == null || !NetworkedTrainCar.TryGetFromTrainCar(coupler.train, out NetworkedTrainCar car))
            return;
        if (coupler.isFrontCoupler)
            car.LastFrontUncouple = cause;
        else
            car.LastRearUncouple = cause;
    }

    internal static void RecordRemoval(NetworkedTrainCar car, ChangeCause<CarRemovalReason> cause)
    {
        recentRemovals.Enqueue((car.CurrentID, car.NetId, cause));
        while (recentRemovals.Count > RecentRemovalCount)
            recentRemovals.Dequeue();
        Multiplayer.Log($"Removed {car.CurrentID} (netId {car.NetId}): {cause}");
        DebugRuntime.Publish("train", "train.car-removed", Side, entityType: "TrainCar",
            entityId: car.NetId.ToString(), data: Data(cause, ("car", car.CurrentID)));
    }

    internal static DebugRuntimeSide Side =>
        NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client;

    internal static Dictionary<string, object> Data<TReason>(ChangeCause<TReason> cause,
        params (string Key, object Value)[] extra) where TReason : struct, Enum
    {
        var data = new Dictionary<string, object>
        {
            ["reason"] = cause.Reason.ToString(),
            ["playerId"] = cause.PlayerId,
            ["player"] = cause.PlayerName,
            ["causeTick"] = cause.Tick,
            ["detail"] = cause.Detail
        };
        foreach ((string key, object value) in extra)
            data[key] = value;
        return data;
    }

    private static class Current<TReason> where TReason : struct, Enum
    {
        internal static ChangeCause<TReason>? Value;

        internal static IDisposable Begin(ChangeCause<TReason> cause)
        {
            ChangeCause<TReason>? previous = Value;
            Value = cause;
            return new Restore(previous);
        }

        private sealed class Restore : IDisposable
        {
            private readonly ChangeCause<TReason>? previous;
            private bool disposed;

            public Restore(ChangeCause<TReason>? previous) => this.previous = previous;

            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                Value = previous;
            }
        }
    }
}
