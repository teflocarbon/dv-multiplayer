using HarmonyLib;
using Multiplayer.Components.Networking.World;

namespace Multiplayer.Patches.World;

/// <summary>
/// Clients don't simulate hazmat tiles: the host sends them (see <see cref="HazmatSync"/>).
/// Vanilla's update loop still runs on clients for the terrain splats and cleanup; the steps that
/// move liquid, take in local leaks and evolve reactions are skipped, and igniting a tile asks
/// the host.
/// </summary>
[HarmonyPatch(typeof(HazmatTileManager))]
public static class HazmatTileManagerPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(HazmatTileManager.EvolveLiquidCellularAutomaton))]
    private static bool EvolveLiquidCellularAutomaton() => !HazmatSync.IsClient;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(HazmatTileManager.UpdateTileLiquidContentExternal))]
    private static bool UpdateTileLiquidContentExternal(HazmatTileManager __instance)
    {
        if (!HazmatSync.IsClient)
            return true;
        // Leaks on this game feed the host's tiles there; drop the local copy.
        __instance.tileLiquidAmountToBeAddedDictionary.Clear();
        __instance.tileLiquidTypeToBeAddedDictionary.Clear();
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(HazmatTileManager.PrepareReaction))]
    private static bool PrepareReaction(HazmatTileManager __instance)
    {
        if (!HazmatSync.IsClient)
            return true;
        __instance.explosionEpicenters.Clear();
        return false;
    }

    // Effects follow the host's tiles as they arrive, and the radiation effect with them.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(HazmatTileManager.ProcessReaction))]
    private static bool ProcessReaction(HazmatTileManager __instance)
    {
        if (!HazmatSync.IsClient)
            return true;
        __instance.reactionTimer = 0f;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(HazmatTileManager.IgniteTerrainTile))]
    private static bool IgniteTerrainTile(HazmatGridTile tile, float ignitionStrength, ref bool __result)
    {
        if (!HazmatSync.IsClient || HazmatSync.ApplyingHostTiles)
            return true;
        HazmatSync.Client_ForwardIgnite(tile, ignitionStrength);
        __result = false;
        return false;
    }
}
