using System;
using DV.Simulation.Controllers;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using UnityEngine;

namespace Multiplayer.Client.Trains;

/// <summary>
/// A client plays the host's explosions and sets off none of its own (see <see cref="ExplosionSync"/>).
/// </summary>
public static class ExplosionPlayback
{
    // Set while a client plays an explosion the host sent.
    internal static bool ApplyingHostExplosion;

    public static void Play(TrainCar car, ExplosionSync.Kind kind, int index)
    {
        if (car == null)
            return;
        ApplyingHostExplosion = true;
        try
        {
            switch (kind)
            {
                case ExplosionSync.Kind.Cargo:
                    if (Pick(ExplosionSync.CargoReactions(car), index) is { } cargo)
                        cargo.ExplodeCargo();
                    else
                        // No interior loaded here (the car is far away): still show the wreck.
                        TrainCarExplosion.UpdateModelToExploded(car);
                    break;
                case ExplosionSync.Kind.Resource:
                    if (Pick(car.GetComponentsInChildren<ResourceExplosionBase>(true), index) is { } resource)
                        resource.ExplodeResource();
                    break;
                case ExplosionSync.Kind.Signal:
                    if (Pick(car.GetComponentsInChildren<ExplosionActivationOnSignal>(true), index) is { } signal)
                        AccessTools.Method(typeof(ExplosionActivationOnSignal), "OnExplosionSignalChanged")
                            .Invoke(signal, new object[] { 1f });
                    break;
            }
        }
        catch (Exception e)
        {
            Multiplayer.LogWarning($"Playing the host's explosion on {car.ID} ({kind}) failed: {e.Message}");
        }
        finally
        {
            ApplyingHostExplosion = false;
        }
    }

    private static T Pick<T>(T[] all, int index) where T : class =>
        all.Length == 0 ? null : all[Mathf.Clamp(index, 0, all.Length - 1)];
}

[HarmonyPatch(typeof(TrainCarExplosion), "ApplyExplosionForceToTrains")]
public static class TrainCarExplosionForcePatch
{
    // Clients never push, damage or derail trains with an explosion; the host does.
    private static bool Prefix() => !NetworkRole.IsRemoteClient;
}

[HarmonyPatch(typeof(CargoReactionBase), nameof(CargoReactionBase.ExplodeCargo))]
public static class CargoReactionExplodePatch
{
    private static readonly AccessTools.FieldRef<CargoReactionBase, bool> AboutToExplode =
        AccessTools.FieldRefAccess<CargoReactionBase, bool>("aboutToExplode");

    private static bool Prefix(CargoReactionBase __instance)
    {
        if (!NetworkRole.IsRemoteClient || ExplosionPlayback.ApplyingHostExplosion)
            return true;
        // The client's own countdown ran out; the host's decides. Stop counting here.
        AboutToExplode(__instance) = false;
        return false;
    }
}

[HarmonyPatch(typeof(ResourceExplosionBase), nameof(ResourceExplosionBase.ExplodeResource))]
public static class ResourceExplosionPatch
{
    private static bool Prefix() => !NetworkRole.IsRemoteClient || ExplosionPlayback.ApplyingHostExplosion;
}

[HarmonyPatch(typeof(ExplosionActivationOnSignal), "OnExplosionSignalChanged")]
public static class ExplosionActivationOnSignalPatch
{
    private static bool Prefix() => !NetworkRole.IsRemoteClient || ExplosionPlayback.ApplyingHostExplosion;
}
