using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using HarmonyLib;
using Multiplayer.Shared.Session;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// Gadget logic that drives a train runs on the server only (gadgets.md, "Logic that writes the
/// train"): on a client with the cab loaded, a gadget's Controls.Move scrolls the player's own lever,
/// which the mod sends as the player's input, so running it here as well would act twice. Off on a
/// remote client for the gadgets the server sent (GadgetJoinApplier.ServerSent), which the server
/// runs. Since gadget PR 3a every
/// gadget a remote client has came from the server (its own installs are requests); a label, which
/// still installs locally until PR 9, keeps vanilla's logic here. The distance tracker isn't here: it only counts, which this game
/// does too between the server's updates.
/// </summary>
[HarmonyPatch]
internal static class GadgetServerOnlyPatches
{
    private static bool ServerRuns(GadgetBase gadget) =>
        NetworkRole.IsRemoteClient && GadgetJoinApplier.IsServerSent(gadget);

    // The countdown shows here between the server's updates (GadgetRunningClient); the braking at
    // its end is the server's.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetATS), "Update")]
    private static bool AtsUpdate(GadgetATS __instance)
    {
        if (!ServerRuns(__instance))
            return true;
        if (__instance.IsActive)
            __instance.currentTimer -= Time.deltaTime;
        else
            __instance.currentTimer = __instance.CurrentRegimeTimerLength;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetAntiSlip), "Update")]
    private static bool AntiSlipUpdate(GadgetAntiSlip __instance) => !ServerRuns(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetOverheatProtection), "Update")]
    private static bool OverheatUpdate(GadgetOverheatProtection __instance) => !ServerRuns(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AlternatingController), "Update")]
    private static bool AlternatingUpdate(AlternatingController __instance) => !ServerRuns(__instance);

    // Its display (Update) reads the limit's effect from the synced port; only the port write is the server's.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetAmpLimiter), "UpdateLimit")]
    private static bool AmpLimiterWrite(GadgetAmpLimiter __instance) => !ServerRuns(__instance);

    // A collision knocks loose gadgets off at random: the server's roll is the only one.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TrainCarCustomization), "OnCollisionEnter")]
    private static bool CollisionDislodge(TrainCarCustomization __instance) =>
        !NetworkRole.IsRemoteClient || !__instance.Customizers.OfType<GadgetBase>().Any(GadgetJoinApplier.IsServerSent);

    // Off its car it's nobody's: if this game installs it again itself, that install is its own.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Customization.CustomizerBase), nameof(Customization.CustomizerBase.Unlink))]
    private static void Unlinked(Customization.CustomizerBase __instance)
    {
        if (__instance is GadgetBase gadget)
            GadgetJoinApplier.ServerSent.Remove(gadget);
    }
}
