using System;
using DV.Simulation.Controllers;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Shared.Trains;

/// <summary>
/// Explosions are the host's. Every game used to run its own: hazmat cargo counts down a random
/// delay on each game separately, and a loco with fuel blew up wherever its health reached zero,
/// so explosions happened at different times on each screen, each pushing and derailing its own
/// copies of the trains (cars blown off the rails on one screen only). Engine-failure explosions
/// come from the loco simulation, which only runs on the host, so clients saw none of those.
///
/// Now clients never apply explosion forces to trains and never set an explosion off themselves.
/// The host tells them when a car explodes (Server/Trains/ExplosionServerPatch.cs) and they play
/// it (Client/Trains/ExplosionClientPatch.cs): effects, sound, the burnt-out model and the push on
/// their own player. The trains move on the host and reach them through the train sync. This holds
/// what both sides name an explosion by: its kind and its index among the car's components of that
/// kind.
/// </summary>
public static class ExplosionSync
{
    public enum Kind : byte { Cargo, Resource, Signal }

    internal static readonly AccessTools.FieldRef<CargoReactionBase, TrainCar> ReactionCar =
        AccessTools.FieldRefAccess<CargoReactionBase, TrainCar>("trainCar");

    // Cargo reactions live on the car's interior, which isn't under the car in the hierarchy.
    internal static CargoReactionBase[] CargoReactions(TrainCar car) =>
        car.interior != null ? car.interior.GetComponentsInChildren<CargoReactionBase>(true) : car.GetComponentsInChildren<CargoReactionBase>(true);

    /// <summary>The car's components that explode as <paramref name="kind"/>, in the order both sides index them.</summary>
    internal static Component[] Components(TrainCar car, Kind kind) => kind switch
    {
        Kind.Cargo => CargoReactions(car),
        Kind.Resource => car.GetComponentsInChildren<ResourceExplosionBase>(true),
        _ => car.GetComponentsInChildren<ExplosionActivationOnSignal>(true),
    };

    /// <summary>The car an exploding component belongs to.</summary>
    internal static TrainCar CarOf(Component component) =>
        component is CargoReactionBase reaction ? ReactionCar(reaction) : TrainCar.Resolve(component.transform);
}

[HarmonyPatch(typeof(CargoEffects), nameof(CargoEffects.OnCargoExploded))]
public static class CargoExplosionAudioPatch
{
    // Vanilla's cargo explosion sounds are 2D: the near and far layers' spatial blend curves are flat
    // at 0, and a 2D source ignores distance, so every player heard each hazmat explosion at full
    // volume wherever they were. Their distances are set up for 3D, as on the loco explosion prefab.
    // Setting spatialBlend replaces the curve, making them 3D so they fall off with distance.
    private static void Prefix(CargoEffects __instance)
    {
        if (__instance.explosionAudio == null)
            return;
        foreach (AudioSource source in __instance.explosionAudio)
            if (source != null)
                source.spatialBlend = 1f;
    }
}
