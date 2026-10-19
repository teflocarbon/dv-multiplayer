using DV.TerrainSystem;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using GridCell = DV.TerrainSystem.TerrainGrid.GridCell;

namespace Multiplayer.Server.Coverage;

/// <summary>
/// Lets ServerTerrainCoverage keep extra terrain cells loaded on a server, beyond the box DV keeps
/// around its one primary point. Nothing changes unless that's active (multiplayer servers).
/// See docs/design/multi-anchor-streaming.md §2.3.
/// </summary>
[HarmonyPatch(typeof(TerrainGrid))]
public static class TerrainGridPatch
{
    /// <summary>The cells the grid keeps loaded include the extra ones.</summary>
    [HarmonyPostfix]
    [HarmonyPatch("GetCellsInRegion")]
    private static void GetCellsInRegion(TerrainGrid __instance, HashSet<GridCell> toPopulate)
    {
        // Only the set of cells to keep (SetTargetCoord); the same call also picks the cells to
        // display in the box (UpdateWorld), which stays DV's.
        ServerTerrainCoverage coverage = ServerTerrainCoverage.Active;
        if (coverage != null && toPopulate == __instance.toLoad)
            foreach (GridCell cell in __instance.grid)
                if (coverage.IsExtra(cell))
                    toPopulate.Add(cell);
    }

    /// <summary>
    /// The box moving off a cell doesn't unload an extra one: it's kept, and if DV had displayed it,
    /// its terrain is swapped for ours.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch("GetActiveCellsOutsideRegion")]
    private static void GetActiveCellsOutsideRegion(HashSet<GridCell> toPopulate)
    {
        ServerTerrainCoverage coverage = ServerTerrainCoverage.Active;
        if (coverage == null)
            return;
        foreach (GridCell cell in toPopulate.Where(coverage.IsExtra).ToList())
        {
            toPopulate.Remove(cell);
            if (cell.GetStatus() == TerrainGrid.LoadingStatus.Displayed)
                coverage.TakeOver(cell);
        }
    }

    /// <summary>
    /// Before the box is displayed, cells inside it give back our terrains (extra or not: the set may
    /// have changed since), so DV displays them with its own (which render, with vegetation).
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("UpdateWorld")]
    private static void UpdateWorld(TerrainGrid __instance)
    {
        ServerTerrainCoverage coverage = ServerTerrainCoverage.Active;
        if (coverage == null)
            return;
        foreach (GridCell cell in __instance.grid)
            if (coverage.InPrimaryBox(cell.coord) && coverage.IsOurs(cell.terrain))
                coverage.HandBack(cell);
    }

    /// <summary>An extra cell counts as in the region: its terrain is found and its cell checked.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(TerrainGrid.IsInTargetRegion), typeof(Vector2Int))]
    private static void IsInTargetRegion(Vector2Int gridCoord, ref bool __result)
    {
        if (!__result && ServerTerrainCoverage.Active is { } coverage && coverage.IsExtra(gridCoord))
            __result = true;
    }

    /// <summary>
    /// Vanilla's "loaded region" means nothing is loading anywhere. Extra cells load whenever a
    /// player crosses a cell or a car derails, which would hold up everything that waits on it
    /// (enabling items near the local player, the item location forcer). Inside the box only the
    /// box's own loading counts; an extra cell is loaded once it's displayed.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(nameof(TerrainGrid.IsInLoadedRegion))]
    private static bool IsInLoadedRegion(TerrainGrid __instance, Vector3 worldPosition, ref bool __result)
    {
        ServerTerrainCoverage coverage = ServerTerrainCoverage.Active;
        if (coverage == null || !__instance.IsInTargetRegion(worldPosition))
            return true;
        Vector2Int coord = __instance.ToGridCoords(worldPosition);
        if (coverage.InPrimaryBox(coord))
            __result = __instance.currentCoord == __instance.targetCoord &&
                       !__instance.queue.Any(cell => coverage.InPrimaryBox(cell.coord)) &&
                       !__instance.inProgress.Any(cell => coverage.InPrimaryBox(cell.coord));
        else
            __result = __instance.grid[__instance.ToIndex(coord)].GetStatus() == TerrainGrid.LoadingStatus.Displayed;
        return false;
    }
}

/// <summary>
/// A cell the grid unloads while showing one of ServerTerrainCoverage's terrains returns it to
/// that pool, never DV's: DV would display its own box with a terrain that doesn't render.
/// </summary>
[HarmonyPatch(typeof(GridCell), "HideTerrain")]
public static class TerrainGridCellHideTerrainPatch
{
    private static bool Prefix(GridCell __instance)
    {
        ServerTerrainCoverage coverage = ServerTerrainCoverage.Active;
        if (coverage == null || !coverage.IsOurs(__instance.terrain))
            return true;
        coverage.Release(__instance.terrain);
        __instance.terrain = null;
        return false;
    }
}
