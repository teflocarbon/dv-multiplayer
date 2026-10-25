using DV.Items;
using HarmonyLib;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// A tool's ammo loaded in the tool's first frame (a take-out makes the tool from its row and its
/// ammo with it, <see cref="StandInAmmo"/>; the server makes its own copy the same way). The tool
/// shows its ammo through dynamic renderers (a paint sprayer's can, a boombox's cassette), which set
/// themselves up in their Start: told of the ammo's mesh and materials before it, they threw, and the
/// load with them (a sprayer taken out had lost its can; a boombox's cassette was in but not shown).
/// Vanilla's load waits a frame, so it never sees this. One told before its Start sets itself up
/// first; its Start then finds it done.
/// </summary>
[HarmonyPatch(typeof(ItemRendererDynamic))]
internal static class DynamicRendererSetupPatch
{
    [HarmonyPatch(nameof(ItemRendererDynamic.UpdateDynamicMesh))]
    [HarmonyPrefix]
    private static void BeforeMesh(ItemRendererDynamic __instance) => SetUpFirst(__instance);

    [HarmonyPatch(nameof(ItemRendererDynamic.UpdateDynamicMaterialsCache))]
    [HarmonyPrefix]
    private static void BeforeMaterials(ItemRendererDynamic __instance) => SetUpFirst(__instance);

    private static void SetUpFirst(ItemRendererDynamic renderer)
    {
        if (!renderer.initialized)
            renderer.Initialize();
    }
}
