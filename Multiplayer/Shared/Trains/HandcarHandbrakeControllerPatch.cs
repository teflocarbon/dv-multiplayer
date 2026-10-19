using DV.CabControls;
using DV.HUD;
using DV.KeyboardInput;
using DV.Simulation.Controllers;
using HarmonyLib;

namespace Multiplayer.Shared.Trains;

/// <summary>
/// Vanilla springs a handcar's handbrake off every frame unless this game's own player is holding
/// it, on every game, so the host's copy (or another client's) undid a player holding it on theirs.
/// Like the other per-frame local-input writers, it's left off while another player is using the
/// control. Handbrakes take no lease, so that's another player's handbrake change seen within the
/// last second (<see cref="NetworkedTrainCar.HandbrakeHeldRemotely"/>), and a player holding it on
/// resends it to keep it so. Once nobody holds it, the game of whoever let go springs it back and
/// the others follow its changes.
/// </summary>
[HarmonyPatch(typeof(HandcarHandbrakeController), "Update")]
public static class HandcarHandbrakeControllerPatch
{
    private static bool Prefix(bool ___initialized, TrainCar ___car, ControlImplBase ___handbrakeInteractable,
        MouseScrollKeyboardInput ___mouseScrollKeyboardInput, InteriorControlsManager ___interiorControlsManager)
    {
        if (!___initialized || ___car == null ||
            !NetworkedTrainCar.TryGetFromTrainCar(___car, out NetworkedTrainCar networkedCar))
            return true;

        // Vanilla's test for its own player holding it.
        bool heldHere = ___handbrakeInteractable.IsGrabbedOrHoverScrolled() ||
                        (___mouseScrollKeyboardInput != null && ___mouseScrollKeyboardInput.IsScrollingInProgress) ||
                        (___interiorControlsManager != null &&
                         ___interiorControlsManager.IsControlScrolledRecently(InteriorControlsManager.ControlType.Handbrake));
        if (heldHere)
        {
            if (___car.brakeSystem.handbrakePosition > 0f)
                networkedCar.Common_HoldingHandbrake();
            return true;
        }
        return !networkedCar.HandbrakeHeldRemotely;
    }
}
