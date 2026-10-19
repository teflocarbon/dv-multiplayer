using System;
using DV.Simulation.Controllers;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using UnityEngine;

namespace Multiplayer.Server.Trains;

/// <summary>
/// The host's explosions, sent to the players as they happen (see <see cref="ExplosionSync"/>).
/// </summary>
public static class ExplosionServer
{
    internal static void Exploded(Component component, ExplosionSync.Kind kind)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || !lifecycle.IsHost() || lifecycle.Server == null || component == null)
            return;
        TrainCar car = ExplosionSync.CarOf(component);
        if (car == null || !car.TryNetworked(out NetworkedTrainCar networked))
            return;
        int index = Array.IndexOf(ExplosionSync.Components(car, kind), component);
        lifecycle.Server.SendExplosion(networked.NetId, kind, (byte)Math.Max(0, index));
    }
}

[HarmonyPatch(typeof(CargoReactionBase), nameof(CargoReactionBase.ExplodeCargo))]
public static class CargoReactionExplodeServerPatch
{
    private static void Postfix(CargoReactionBase __instance) =>
        ExplosionServer.Exploded(__instance, ExplosionSync.Kind.Cargo);
}

[HarmonyPatch(typeof(ResourceExplosionBase), nameof(ResourceExplosionBase.ExplodeResource))]
public static class ResourceExplosionServerPatch
{
    private static void Postfix(ResourceExplosionBase __instance) =>
        ExplosionServer.Exploded(__instance, ExplosionSync.Kind.Resource);
}

[HarmonyPatch(typeof(ExplosionActivationOnSignal), "OnExplosionSignalChanged")]
public static class ExplosionActivationOnSignalServerPatch
{
    private static void Postfix(ExplosionActivationOnSignal __instance, float explosionSignal)
    {
        if (explosionSignal == 1f && AStartGameData.carsAndJobsLoadingFinished)
            ExplosionServer.Exploded(__instance, ExplosionSync.Kind.Signal);
    }
}
