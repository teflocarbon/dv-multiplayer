using HarmonyLib;

namespace Multiplayer.Client.Players;

[HarmonyPatch(typeof(MapMarkersController), nameof(MapMarkersController.Awake))]
public static class MapMarkersController_Awake_Patch
{
    private static void Postfix(MapMarkersController __instance)
    {
        __instance.gameObject.AddComponent<NetworkedMapMarkersController>();
    }
}
