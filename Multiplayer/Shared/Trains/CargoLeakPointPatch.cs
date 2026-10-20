using HarmonyLib;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Shared.Trains;

/// <summary>
/// Where a damaged tank car leaks from (#76). When a car's cargo is set up, vanilla picks one of
/// the cargo model's possible rupture points with UnityEngine.Random, so each game rolled its own:
/// a car leaked from the back on one screen and from the side on another, until the spill caught
/// fire (hazmat tiles are the host's, HazmatSync).
///
/// In multiplayer the pick is seeded from the car's ID, which every game agrees on (not its cargo,
/// which may reach a client a moment after the setup), so all games choose the same point with no
/// packets. Unity's random state is put back afterwards, so nothing else's randomness changes.
/// </summary>
[HarmonyPatch(typeof(CargoEffects), "SetRandomEffectsPosition")]
public static class CargoLeakPointPatch
{
    private static readonly AccessTools.FieldRef<CargoEffects, TrainCar> TrainCarField =
        AccessTools.FieldRefAccess<CargoEffects, TrainCar>("trainCar");

    private static void Prefix(CargoEffects __instance, out Random.State? __state)
    {
        __state = null;
        if (NetworkLifecycle.Instance == null ||
            !NetworkLifecycle.Instance.IsSessionRunning)
            return;
        TrainCar car = TrainCarField(__instance);
        if (car == null || string.IsNullOrEmpty(car.ID))
            return;
        __state = Random.state;
        Random.InitState(StableSeed(car.ID));
    }

    private static void Postfix(Random.State? __state)
    {
        if (__state.HasValue)
            Random.state = __state.Value;
    }

    // FNV-1a over the ID's characters: string.GetHashCode isn't the same in every process.
    internal static int StableSeed(string id)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in id)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)hash;
        }
    }
}
