using DV;
using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Trains;

// The server prices a rerail with the radio's newbie discount, which the host's own rerailer holds.
[HarmonyPatch(typeof(RerailController))]
public static class RerailControllerServerPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(RerailController.Awake))]
    private static void Awake_Postfix(RerailController __instance)
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        NetworkLifecycle.Instance.Server.rerailController = __instance;
    }
}
