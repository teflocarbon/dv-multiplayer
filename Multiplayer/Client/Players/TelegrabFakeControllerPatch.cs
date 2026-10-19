using DV.VR;
using HarmonyLib;
using VRTK;

namespace Multiplayer.Client.Players;

/// <summary>
/// Vanilla VR bug: telekinesis on a lever or dial parents the hand's one fake controller under the
/// control. If the control is destroyed mid-grab, as when the player steps from a loco into its tender
/// and the loco's interior unloads, the control's OnDisable stops the grab but Unity refuses to unparent
/// the fake controller ("Cannot set the parent ... while activating or deactivating the parent"), so it
/// is destroyed along with the interior. Every later control telegrab on that hand then throws in
/// GetFakeController, leaving the handler stuck on its target: control telekinesis on that hand stays
/// broken until the game restarts, while item telekinesis (a separate path) keeps working.
///
/// Build a new fake controller when the old one has been destroyed, as the handler's Awake does.
/// </summary>
[HarmonyPatch(typeof(TelegrabInteractionHandler), nameof(TelegrabInteractionHandler.GetFakeController))]
public static class TelegrabFakeControllerPatch
{
    private static readonly AccessTools.FieldRef<TelegrabInteractionHandler, VRTK_InteractGrab> FakeController =
        AccessTools.FieldRefAccess<TelegrabInteractionHandler, VRTK_InteractGrab>("fakeController");

    private static void Prefix(TelegrabInteractionHandler __instance)
    {
        if (FakeController(__instance) != null)
            return;
        Multiplayer.LogWarning($"Telegrab fake controller on {__instance.name} was destroyed with the control it held, rebuilding it");
        AccessTools.Method(typeof(TelegrabInteractionHandler), "CreateFakeController").Invoke(__instance, null);
    }
}
