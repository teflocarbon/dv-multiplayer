using HarmonyLib;
using Multiplayer.Components.Networking;
using System.Collections.Generic;

namespace Multiplayer.Patches.World;

/// <summary>
/// DV's Far streamer unloads every tile outside a range of its one tracker. On a server, tiles
/// ServerSceneryCoverage still wants (around a remote player or a derailed car) are taken out of its
/// list for the unload pass and put back after, so it only unloads what nobody wants.
/// See docs/design/multi-anchor-streaming.md §1.4.
/// </summary>
[HarmonyPatch(typeof(Streamer), "SceneUnloading")]
public static class StreamerPatch
{
    private static void Prefix(Streamer __instance, out List<SceneSplit> __state)
    {
        __state = null;
        ServerSceneryCoverage coverage = ServerSceneryCoverage.Instance;
        if (coverage == null || !coverage.Covers(__instance))
            return;
        __state = __instance.loadedScenes.FindAll(coverage.Keeps);
        if (__state.Count > 0)
            __instance.loadedScenes.RemoveAll(coverage.Keeps);
    }

    private static void Postfix(Streamer __instance, List<SceneSplit> __state)
    {
        if (__state != null && __state.Count > 0)
            __instance.loadedScenes.AddRange(__state);
    }
}
