using HarmonyLib;
using Multiplayer.Components.Networking.Player;
using Multiplayer.Utils;
using System;

namespace Multiplayer.Patches.Player;

[HarmonyPatch(typeof(CustomFirstPersonController))]
public static class CustomFirstPersonControllerPatch
{
    public static Action OnJump;

    [HarmonyPatch(nameof(CustomFirstPersonController.Awake))]
    [HarmonyPostfix]
    private static void Awake(CustomFirstPersonController __instance)
    {
        LocalPlayerTrackerBase tracker;

        if (VRManager.IsVREnabled())
            tracker = __instance.GetOrAddComponent<LocalPlayerTrackerVR>();
        else
            tracker = __instance.GetOrAddComponent<LocalPlayerTrackerNonVR>();

        if (tracker == null)
            Multiplayer.LogError("Failed to add LocalPlayerTracker to CustomFirstPersonController");
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CustomFirstPersonController.SetJumpParameters))]
    private static void SetJumpParameters()
    {
        OnJump?.Invoke();
    }

#if DEBUG
    // Rewired continues reporting raw mouse delta while a managed test game is in the
    // background. Unlocking Unity's cursor therefore prevents capture but does not stop
    // DV's first-person controller from rotating the camera. Suppress only the native
    // human-look step for harness-managed launches; teleport and explicit test rotations
    // use separate controller methods and remain available.
    [HarmonyPrefix]
    [HarmonyPatch("RotateView")]
    private static bool RotateViewForManagedHarness()
    {
        return !global::Multiplayer.Debugging.DebugRuntime.PreventCursorCapture;
    }
#endif
}
