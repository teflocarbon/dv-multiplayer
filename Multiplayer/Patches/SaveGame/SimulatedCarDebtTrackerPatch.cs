using System.Linq;
using DV.Damage;
using DV.ServicePenalty;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;

namespace Multiplayer.Patches.SaveGame;

/// <summary>
/// Paying a loco's debt (or just selecting it on the fees screen) resets it: power off, throttle
/// and brakes released, handbrake on, reverser to neutral, across its whole consist. Harmless in
/// single player, where the player is at the career manager; in multiplayer the host pays for
/// every loco anyone has used, so paying stopped the trains other players were driving. Leave a
/// consist that's moving or has someone aboard alone; the repair and refill still happen.
/// </summary>
[HarmonyPatch(typeof(SimulatedCarDebtTracker), nameof(SimulatedCarDebtTracker.TurnOffDebtSources))]
public static class SimulatedCarDebtTrackerPatch
{
    private static readonly AccessTools.FieldRef<SimulatedCarDebtTracker, DamageController> DamageController =
        AccessTools.FieldRefAccess<SimulatedCarDebtTracker, DamageController>("dmgController");

    private static bool Prefix(SimulatedCarDebtTracker __instance)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || !lifecycle.IsHost() || lifecycle.Server == null || (lifecycle.Server.PlayerCount <= 1 && !lifecycle.IsDedicatedHost))
            return true;

        DamageController damage = DamageController(__instance);
        TrainCar car = damage != null ? TrainCar.Resolve(damage.gameObject) : null;
        if (car == null || car.trainset == null)
            return true;

        bool inUse = car.trainset.cars.Any(c => c != null &&
            (UnityEngine.Mathf.Abs(c.GetForwardSpeed()) > 0.1f ||
             (NetworkedTrainCar.TryGetFromTrainCar(c, out NetworkedTrainCar net) && net.HasPlayers)));
        if (inUse)
            Multiplayer.Log($"Debt paid on {car.ID}: leaving its controls alone, the train is in use");
        return !inUse;
    }
}
