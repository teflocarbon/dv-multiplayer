using DV.VRTK_Extensions;
using HarmonyLib;
using UnityEngine;
using VRTK;

namespace Multiplayer.Patches.Player;

/// <summary>
/// Vanilla VR leak (#78): DV's near-touch re-initialises every time a hand controller's model becomes
/// available again, which is every time the controller is switched back on, such as after the SteamVR
/// dashboard or Desktop+ took input focus. Each time it makes a new near-touch collider container (a
/// trigger with a rigidbody) and leaves the old one under the controller. They pile up at the hands,
/// all overlapping: after an hour or two of dashboard visits, 70 per hand, and thousands of trigger
/// pairs every physics step. The frame rate collapsed when the hands came together (to about 8 fps),
/// and the dashboard seemed to fix it because it switches the controllers, and every copy, off.
///
/// Destroy the old container before the new one is made, as VRTK's own OnDisable does.
/// </summary>
[HarmonyPatch(typeof(VRTK_InteractNearTouch_DV), "CreateNearTouchCollider")]
public static class NearTouchColliderLeakPatch
{
    private static readonly AccessTools.FieldRef<VRTK_InteractNearTouch, GameObject> Container =
        AccessTools.FieldRefAccess<VRTK_InteractNearTouch, GameObject>("neartouchColliderContainer");

    private static void Prefix(VRTK_InteractNearTouch_DV __instance)
    {
        GameObject old = Container(__instance);
        if (old == null)
            return;
        Object.Destroy(old);
        Container(__instance) = null;
    }
}
