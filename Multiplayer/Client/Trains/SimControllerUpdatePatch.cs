using DV;
using DV.Simulation.Cars;
using DV.Utils;
using HarmonyLib;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Client.Trains;

/// <summary>
/// A client's car shows its simulation's outputs here, once a frame before its simulation
/// controllers run (a handcar's crank is drawn by one): the host's values played out smoothly,
/// and the ones the local player's controls drive worked out locally (<see cref="TrainPortPresenter"/>).
/// The components themselves stay blocked on clients (<see cref="SimComponent_Tick_Patch"/>).
/// </summary>
[HarmonyPatch(typeof(SimController), "Update")]
public static class SimControllerUpdatePatch
{
    private static void Prefix(SimController __instance)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || !NetworkRole.IsRemoteClient)
            return;
        // As SimController.Update: nothing moves while time stands still.
        if (!TimeUtil.IsFlowing || SingletonBehaviour<PausePhysicsHandler>.Instance.PhysicsHandlingInProcess)
            return;
        if (__instance.SimulationFlow != null)
            TrainPortPresenter.OnSimUpdate(__instance.SimulationFlow, Time.deltaTime * __instance.simTimeMultiplier);
    }
}
