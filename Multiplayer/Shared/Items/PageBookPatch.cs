using HarmonyLib;

namespace Multiplayer.Shared.Items;

[HarmonyPatch(typeof(PageBook))]
public static class PageBookPatch
{
    [HarmonyPatch("Start")]
    [HarmonyPrefix]
    private static void Start(PageBook __instance)
    {
        NetworkedItem item = __instance.GetComponentInParent<NetworkedItem>();
        if (item != null) NetworkedPageBookState.TryRegister(item, __instance);
    }
}
