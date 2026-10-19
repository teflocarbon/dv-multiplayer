using DV.LocoRestoration;
using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Trains;

// The host watches each restoration loco's progress to send it to the players (NetworkRestorationWatcher).
[HarmonyPatch(typeof(LocoRestorationController))]
public static class LocoRestorationControllerStartPostfixPatch
{
    [HarmonyPatch(nameof(LocoRestorationController.Start))]
    [HarmonyPostfix]
    private static void Start(LocoRestorationController __instance)
    {
        if (NetworkLifecycle.Instance.IsHost())
            NetworkRestorationWatcher.Instance.AddController(__instance);
    }
}
