using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;

namespace Multiplayer.Client.Trains;

// A remote client follows the host's bogies: each one gets the queue NetworkedTrainCarClient
// feeds, and they never switch junctions themselves (the host's junction state arrives).
[HarmonyPatch(typeof(Bogie))]
public static class BogieFollowerPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(Bogie.SetupPhysics))]
    private static void SetupPhysics(Bogie __instance)
    {
        if (NetworkRole.IsRemoteClient)
            __instance.gameObject.GetOrAddComponent<NetworkedBogie>();
    }

    // Vanilla Bogie.Start skips SetupPhysics for cars that load derailed, so the postfix above
    // never ran for them: the client's NetworkedTrainCar waited forever for its bogies, never
    // initialised, train topology sync timed out and the client was disconnected. Career saves
    // always contain derailed restoration locos, so joining any career save failed.
    [HarmonyPostfix]
    [HarmonyPatch("Start")]
    private static void Start(Bogie __instance)
    {
        if (NetworkRole.IsRemoteClient)
            __instance.gameObject.GetOrAddComponent<NetworkedBogie>();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(Bogie.SwitchJunctionIfNeeded))]
    private static bool SwitchJunctionIfNeeded() => !NetworkRole.IsRemoteClient;
}
