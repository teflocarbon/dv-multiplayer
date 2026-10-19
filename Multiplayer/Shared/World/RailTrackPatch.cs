using HarmonyLib;

namespace Multiplayer.Shared.World;

[HarmonyPatch(typeof(RailTrack), nameof(RailTrack.Awake))]
public static class RailTrack_Awake_Patch
{
    private static void Prefix(RailTrack __instance)
    {
        if (__instance != null && __instance.GetComponent<NetworkedRailTrack>() == null)
            __instance.gameObject.AddComponent<NetworkedRailTrack>();
    }
}
