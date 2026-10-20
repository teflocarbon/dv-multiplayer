using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Stations;

// The hose's rope is the server's (Server/Stations/PropHoseRopePatch).
[HarmonyPatch(typeof(PropHose))]
public static class PropHoseClientPatch
{
    [HarmonyPatch(nameof(PropHose.OnEnable))]
    [HarmonyPrefix]
    public static bool OnEnable()
    {
        //prevent client from snapping manual service plug to home position
        return !NetworkRole.IsRemoteClient;
    }
}
