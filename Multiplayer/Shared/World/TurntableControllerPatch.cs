using HarmonyLib;
using Multiplayer.Shared.Utils;

namespace Multiplayer.Shared.World;

[HarmonyPatch(typeof(TurntableController), nameof(TurntableController.Awake))]
public static class TurntableController_Awake_Patch
{
    private static void Prefix(TurntableController __instance)
    {
        __instance.turntable.gameObject.GetOrAddComponent<NetworkedTurntable>();
    }
}
