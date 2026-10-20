using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using HarmonyLib;
using Multiplayer.Shared.Session;
using System.Linq;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// Gadget logic that drives a train runs on the server only (gadgets.md, "Logic that writes the
/// train"): on a client with the cab loaded, a gadget's Controls.Move scrolls the player's own lever,
/// which the mod sends as the player's input, so running it here as well would act twice. Off on a
/// remote client for the gadgets the server sent (GadgetJoinApplier.ServerSent), which the server
/// runs. A gadget this game installed itself isn't on the server until gadget PR 3a, so it keeps
/// vanilla's logic here, as before.
/// </summary>
[HarmonyPatch]
internal static class GadgetServerOnlyPatches
{
    private static bool ServerRuns(GadgetBase gadget) =>
        NetworkRole.IsRemoteClient && GadgetJoinApplier.IsServerSent(gadget);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetATS), "Update")]
    private static bool AtsUpdate(GadgetATS __instance) => !ServerRuns(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetAntiSlip), "Update")]
    private static bool AntiSlipUpdate(GadgetAntiSlip __instance) => !ServerRuns(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetOverheatProtection), "Update")]
    private static bool OverheatUpdate(GadgetOverheatProtection __instance) => !ServerRuns(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetRoadrunner), "Update")]
    private static bool RoadrunnerUpdate(GadgetRoadrunner __instance) => !ServerRuns(__instance);

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
