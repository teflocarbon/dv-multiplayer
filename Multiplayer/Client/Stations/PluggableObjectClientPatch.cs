using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Stations;

// A remote client's plugs are where the host has them (the station's bulk update), not snapped
// home as they wake.
[HarmonyPatch(typeof(PluggableObject))]
public static class PluggableObjectClientPatch
{
    [HarmonyPatch(nameof(PluggableObject.Awake))]
    [HarmonyPrefix]
    public static bool Awake(PluggableObject __instance)
    {
        if (!NetworkRole.IsRemoteClient)
            return true;

        // Allow the client to setup the plug, but don't allow `InstantSnapTo(this.startAttachedTo);` to be called
        __instance.CheckInitialization();
        return false;
    }
}
