using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;

namespace Multiplayer.Client.Trains;

// A client follows the host's bogies: each one gets the queue NetworkedTrainCarClient feeds.
[HarmonyPatch(typeof(Bogie))]
public static class BogieFollowerPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(Bogie.SetupPhysics))]
    private static void SetupPhysics(Bogie __instance)
    {
        if (!NetworkLifecycle.Instance.IsHost())
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
        if (!NetworkLifecycle.Instance.IsHost())
            __instance.gameObject.GetOrAddComponent<NetworkedBogie>();
    }
}
