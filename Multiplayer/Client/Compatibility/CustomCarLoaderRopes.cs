using System;
using System.Reflection;
using DV.CabControls;
using HarmonyLib;
using Multiplayer.Client.Trains;

namespace Multiplayer.Client.Compatibility;

/// <summary>
/// Custom Car Loader's horn and whistle ropes write their control every frame from this game's own
/// rope, whoever is pulling it, like vanilla's WhistleRopeController (see
/// <c>NetworkedTrainCarClient.LocalInputWriters</c>). Every game with the cab loaded runs them, so
/// while a player on another game pulls one, this game's wrote it back as let go: the host's cut a
/// client's horn, and a client's cut the host's horn in its own cab. Each skips its update while
/// another player holds the lease on its port, and picks up from the rope as it hangs once they let
/// go.
///
/// <c>PullableRopeBase</c> (the C class, C-16, FEF-3 Modification, S464 and S484) is the control
/// itself: its <c>Update</c> requests the rope's length as the value, and it ignores
/// <see cref="ControlImplBase.SetValue"/>. It can't be switched off as vanilla's is, as its
/// <c>OnDisable</c> writes 0 and snaps the rope back. <c>WhistleDistanceControllerInternal</c> (no
/// known car) writes its dummy control as vanilla's does; it starts again from 0, as vanilla's does
/// once it's switched back on.
/// </summary>
public static class CustomCarLoaderRopes
{
    private const string CCL_ASSEMBLY = "CCL.Importer";
    private const string CCL_PULLABLE_ROPE = "CCL.Importer.Implementations.Controls.PullableRopeBase";
    private const string CCL_WHISTLE_DISTANCE = "CCL.Importer.Components.Controllers.WhistleDistanceControllerInternal";

    private static Harmony harmony;
    private static bool cclPatched;

    public static void Patch(Harmony harmony)
    {
        CustomCarLoaderRopes.harmony = harmony;
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
        Type rope = AccessTools.TypeByName(CCL_PULLABLE_ROPE);
        if (rope == null)
            return false;

        cclPatched = true;
        PatchUpdate(rope, nameof(RopeUpdatePrefix));
        Type whistle = AccessTools.TypeByName(CCL_WHISTLE_DISTANCE);
        if (whistle != null)
            PatchUpdate(whistle, nameof(WhistleDistanceUpdatePrefix));
        Multiplayer.Log("Custom Car Loader's ropes patched");
        return true;
    }

    private static void PatchUpdate(Type type, string prefix)
    {
        MethodInfo update = AccessTools.Method(type, "Update");
        if (update == null)
        {
            Multiplayer.LogWarning($"CustomCarLoaderRopes: {type.FullName}.Update not found, it will cut other players' horns");
            return;
        }
        harmony.Patch(update, prefix: new HarmonyMethod(AccessTools.Method(typeof(CustomCarLoaderRopes), prefix)));
    }

    private static bool RopeUpdatePrefix(ControlImplBase __instance) =>
        !NetworkedTrainCarClient.IsHeldByAnotherPlayer(__instance);

    private static bool WhistleDistanceUpdatePrefix(ControlImplBase ____dummy, ref float ____smoothedValue, ref float ____velocity)
    {
        if (!NetworkedTrainCarClient.IsHeldByAnotherPlayer(____dummy))
            return true;
        ____smoothedValue = 0f;
        ____velocity = 0f;
        return false;
    }
}
