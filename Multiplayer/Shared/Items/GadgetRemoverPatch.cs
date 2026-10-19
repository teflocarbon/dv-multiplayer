using DV.Customization.Gadgets;
using HarmonyLib;

namespace Multiplayer.Shared.Items;

[HarmonyPatch(typeof(GadgetRemover))]
public static class GadgetRemoverPatch
{
    [HarmonyPatch(nameof(GadgetRemover.Remove))]
    [HarmonyPrefix]
    private static void RemoveGadget(GadgetRemover __instance, GadgetBase target)
    {
        target.TryGetComponent<NetworkedItem>(out var netItem);
        netItem?.OnRemove();
        
    }
}
