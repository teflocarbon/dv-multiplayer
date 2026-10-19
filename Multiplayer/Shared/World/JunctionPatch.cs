using HarmonyLib;
using Multiplayer.Shared.Utils;

namespace Multiplayer.Shared.World;

[HarmonyPatch(typeof(Junction), nameof(Junction.Awake))]
public static class Junction_Awake_Patch
{
    private static void Prefix(Junction __instance)
    {
        __instance.gameObject.GetOrAddComponent<NetworkedJunction>();
    }
}
