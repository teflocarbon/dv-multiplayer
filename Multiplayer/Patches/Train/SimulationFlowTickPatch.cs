using HarmonyLib;
using LocoSim.Implementations;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;

namespace Multiplayer.Patches.Train;

/// <summary>
/// A client's car shows its simulation's outputs here, each time its simulation would tick: the
/// host's values played out smoothly, and the ones the local player's controls drive worked out
/// locally (<see cref="TrainPortPresenter"/>). The components themselves stay blocked on clients
/// (<see cref="Multiplayer.Patches.World.SimComponent_Tick_Patch"/>).
/// </summary>
[HarmonyPatch(typeof(SimulationFlow), nameof(SimulationFlow.Tick))]
public static class SimulationFlowTickPatch
{
    private static void Prefix(SimulationFlow __instance, float delta)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || !lifecycle.IsClientRunning || lifecycle.IsHost())
            return;
        TrainPortPresenter.OnFlowTick(__instance, delta);
    }
}
