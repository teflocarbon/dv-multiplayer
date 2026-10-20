using DV.Customization;
using DV.Utils;
using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// In multiplayer a gadget installs on a car or not at all (gadgets.md, "Off-train installs"):
/// the world, the house, the Lost and Found shed and the paint station resolve to no anchor, so
/// vanilla's placement shows its red highlight and places nothing there. The label maker places
/// through the same resolver, and nothing else calls it.
/// </summary>
[HarmonyPatch(typeof(Customization), nameof(Customization.Resolve))]
internal static class GadgetVehiclesOnlyPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Customization __result)
    {
        if (__result == null || __result is TrainCarCustomization ||
            NetworkLifecycle.Instance?.IsSessionRunning != true)
            return;
        __result = null;
        // Vanilla's own placement messages are non-VR only too (GadgetItem.TryPlaceOnHit).
        if (!VRManager.IsVREnabled())
            SingletonBehaviour<InteractionTextControllerNonVr>.Instance.DisplayText(Locale.GADGETS__VEHICLES_ONLY);
    }
}
