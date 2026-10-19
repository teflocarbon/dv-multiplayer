using System;
using System.Reflection;
using DV;
using HarmonyLib;
using Multiplayer.Components.Networking;

namespace Multiplayer.Patches.Train;

/// <summary>
/// Steam locos couple their own tender: vanilla's <see cref="SteamTenderAutoCoupleMechanism"/>
/// and Custom Car Loader's copy of it (<c>CarAutoCouplerInternal</c>, on CCL locos like the
/// A.T.&amp;S.F. 2901) wait 1.5 s after the car starts, then 4 s, then check every 2 s, and couple
/// with <see cref="Coupler.TryCouple"/>. That isn't a chain interaction, so vanilla also connects
/// the hose and opens both cocks. They restart on every rerail and uncouple too, and CCL's also
/// uncouples when its disabler port goes high.
///
/// Every game ran them on its own copy, outside any packet. A loco the host spawned with its tender
/// was coupled by the host and by every client at the same moment, and each client's hose and cock
/// patches sent the host a ConnectHose and two SetCocks its player never made (playtest 2026-10-19:
/// L-072 and C-128 from all three players at once, refused OutOfRange from a kilometre away). On a
/// client these are the host's: its couple or uncouple arrives in the next topology snapshot.
/// </summary>
public static class TenderAutoCouplerPatch
{
    private const string CCL_ASSEMBLY = "CCL.Importer";
    private const string CCL_AUTO_COUPLER = "CCL.Importer.Components.CarAutoCouplerInternal";

    private static Harmony harmony;
    private static bool cclPatched;

    public static void Patch(Harmony harmony)
    {
        TenderAutoCouplerPatch.harmony = harmony;
        PatchMethod(typeof(SteamTenderAutoCoupleMechanism), "StartAutoCoupleCoroIfConditionsFulfilled");

        if (!TryPatchCcl())
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
    }

    private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetName().Name != CCL_ASSEMBLY)
            return;
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        TryPatchCcl();
    }

    private static bool TryPatchCcl()
    {
        if (cclPatched)
            return true;
        Type autoCoupler = AccessTools.TypeByName(CCL_AUTO_COUPLER);
        if (autoCoupler == null)
            return false;

        cclPatched = true;
        PatchMethod(autoCoupler, "StartAutoCoupleCoroIfConditionsFulfilled");
        PatchMethod(autoCoupler, "DisablerValueChanged");
        Multiplayer.Log("Custom Car Loader's auto-coupler patched");
        return true;
    }

    private static void PatchMethod(Type type, string name)
    {
        MethodInfo method = AccessTools.Method(type, name);
        if (method == null)
        {
            Multiplayer.LogWarning($"TenderAutoCouplerPatch: {type.FullName}.{name} not found, clients will auto-couple it themselves");
            return;
        }
        harmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(TenderAutoCouplerPatch), nameof(HostOnly))));
    }

    private static bool HostOnly()
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        return lifecycle == null || !lifecycle.IsClientRunning || lifecycle.IsHost();
    }
}
