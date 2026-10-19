using DV.Platform.Steam;
using HarmonyLib;
using Steamworks;


namespace Multiplayer.Shared.Transport;

[HarmonyPatch(typeof(DVSteamworks))]
public static class DVSteamworksPatch
{
    [HarmonyPatch(nameof(DVSteamworks.Awake))]
    [HarmonyPostfix]
    public static void Awake()
    {
        if (DVSteamworks.Success)
            SteamNetworkingUtils.InitRelayNetworkAccess();
    }
}
