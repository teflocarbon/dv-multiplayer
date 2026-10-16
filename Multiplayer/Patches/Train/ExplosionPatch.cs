using System;
using DV.Simulation.Controllers;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Utils;
using UnityEngine;

namespace Multiplayer.Patches.Train;

/// <summary>
/// Explosions are the host's. Every game used to run its own: hazmat cargo counts down a random
/// delay on each game separately, and a loco with fuel blew up wherever its health reached zero,
/// so explosions happened at different times on each screen, each pushing and derailing its own
/// copies of the trains (cars blown off the rails on one screen only). Engine-failure explosions
/// come from the loco simulation, which only runs on the host, so clients saw none of those.
///
/// Now clients never apply explosion forces to trains and never set an explosion off themselves.
/// The host tells them when a car explodes and they play it: effects, sound, the burnt-out model
/// and the push on their own player. The trains move on the host and reach them through the train
/// sync.
/// </summary>
public static class ExplosionSync
{
    public enum Kind : byte { Cargo, Resource, Signal }

    // Set while a client plays an explosion the host sent.
    internal static bool ApplyingHostExplosion;

    internal static bool IsClient =>
        NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.IsClientRunning && !NetworkLifecycle.Instance.IsHost();

    private static bool IsHostWithPlayers =>
        NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.IsHost() && NetworkLifecycle.Instance.Server != null;

    internal static readonly AccessTools.FieldRef<CargoReactionBase, TrainCar> ReactionCar =
        AccessTools.FieldRefAccess<CargoReactionBase, TrainCar>("trainCar");

    // Cargo reactions live on the car's interior, which isn't under the car in the hierarchy.
    internal static CargoReactionBase[] CargoReactions(TrainCar car) =>
        car.interior != null ? car.interior.GetComponentsInChildren<CargoReactionBase>(true) : car.GetComponentsInChildren<CargoReactionBase>(true);

    internal static void HostExploded(Component component, Kind kind)
    {
        if (!IsHostWithPlayers || component == null)
            return;
        TrainCar car = component is CargoReactionBase reaction ? ReactionCar(reaction) : TrainCar.Resolve(component.transform);
        if (car == null || !car.TryNetworked(out NetworkedTrainCar networked))
            return;
        Component[] all = kind switch
        {
            Kind.Cargo => CargoReactions(car),
            Kind.Resource => car.GetComponentsInChildren<ResourceExplosionBase>(true),
            _ => car.GetComponentsInChildren<ExplosionActivationOnSignal>(true),
        };
        int index = Array.IndexOf(all, component);
        NetworkLifecycle.Instance.Server.SendExplosion(networked.NetId, kind, (byte)Math.Max(0, index));
    }

    public static void ClientPlay(TrainCar car, Kind kind, int index)
    {
        if (car == null)
            return;
        ApplyingHostExplosion = true;
        try
        {
            switch (kind)
            {
                case Kind.Cargo:
                    if (Pick(CargoReactions(car), index) is { } cargo)
                        cargo.ExplodeCargo();
                    else
                        // No interior loaded here (the car is far away): still show the wreck.
                        TrainCarExplosion.UpdateModelToExploded(car);
                    break;
                case Kind.Resource:
                    if (Pick(car.GetComponentsInChildren<ResourceExplosionBase>(true), index) is { } resource)
                        resource.ExplodeResource();
                    break;
                case Kind.Signal:
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
    private static bool Prefix() => !ExplosionSync.IsClient;
}

[HarmonyPatch(typeof(CargoReactionBase), nameof(CargoReactionBase.ExplodeCargo))]
public static class CargoReactionExplodePatch
{
    private static readonly AccessTools.FieldRef<CargoReactionBase, bool> AboutToExplode =
        AccessTools.FieldRefAccess<CargoReactionBase, bool>("aboutToExplode");

    private static bool Prefix(CargoReactionBase __instance)
    {
        if (!ExplosionSync.IsClient || ExplosionSync.ApplyingHostExplosion)
            return true;
        // The client's own countdown ran out; the host's decides. Stop counting here.
        AboutToExplode(__instance) = false;
        return false;
    }

    private static void Postfix(CargoReactionBase __instance) =>
        ExplosionSync.HostExploded(__instance, ExplosionSync.Kind.Cargo);
}

[HarmonyPatch(typeof(ResourceExplosionBase), nameof(ResourceExplosionBase.ExplodeResource))]
public static class ResourceExplosionPatch
{
    private static bool Prefix() => !ExplosionSync.IsClient || ExplosionSync.ApplyingHostExplosion;

    private static void Postfix(ResourceExplosionBase __instance) =>
        ExplosionSync.HostExploded(__instance, ExplosionSync.Kind.Resource);
}

[HarmonyPatch(typeof(ExplosionActivationOnSignal), "OnExplosionSignalChanged")]
public static class ExplosionActivationOnSignalPatch
{
    private static bool Prefix() => !ExplosionSync.IsClient || ExplosionSync.ApplyingHostExplosion;

    private static void Postfix(ExplosionActivationOnSignal __instance, float explosionSignal)
    {
        if (explosionSignal == 1f && AStartGameData.carsAndJobsLoadingFinished)
            ExplosionSync.HostExploded(__instance, ExplosionSync.Kind.Signal);
    }
}
