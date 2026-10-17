using DV.TerrainSystem;
using HarmonyLib;
using Multiplayer.Networking.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using GridCell = DV.TerrainSystem.TerrainGrid.GridCell;

namespace Multiplayer.Components.Networking;

/// <summary>
/// Terrain colliders on the server around every remote player and every derailed car, not only
/// around the one point DV loads terrain for (the local player, or the player a dedicated host
/// follows). Without them a car derailed far from that point that left the track ballast had
/// nothing under it on the server, and was frozen in mid-air (ServerWorldCoverage).
///
/// DV's TerrainGrid keeps a 5×5 box of 512 m cells around its primary point, displayed with a
/// fixed pool of 25 terrains. This adds "extra" cells:
/// - The grid loads them itself: they're added to its set of cells to keep (TerrainGridPatch), and
///   a change of the set re-runs its cell selection.
/// - They're displayed here, with terrains from a pool of our own, which have a collider but don't
///   render (a host's player would otherwise see full-detail terrain far away, over DV's distant
///   terrain). DV's pool stays for its own box.
/// - An extra cell inside the primary box is DV's: it's handed over when the box reaches it, and
///   taken back when the box moves off it, so it never unloads in between.
/// - DV is told terrain changed (TerrainsMoved) when extra cells come or go, so tunnel portals
///   find their cell's terrain (they make cars in the tunnel ignore it), buffer stops and the
///   derail freeze re-check.
///
/// Multiplayer servers only. See docs/design/multi-anchor-streaming.md §2.
/// </summary>
public sealed class ServerTerrainCoverage : MonoBehaviour
{
    // DV's own box around its primary point is this ring; players get the same.
    private const int PlayerRing = 2;
    // A derailed car gets the cells within this distance of it, so it can slide off an edge.
    private const float DerailedCarMargin = 64f;
    // A cap on extra cells (about 1.6 MB each): derailed cars first, then players nearest first.
    private const int MaxExtraCells = 100;
    private const float RecheckSeconds = 0.5f;
    private const int MaxDisplaysPerFrame = 2;

    private static readonly FieldInfo terrainsMovedField = AccessTools.Field(typeof(TerrainGrid), nameof(TerrainGrid.TerrainsMoved));
    private static readonly FieldInfo terrainDataLoadedField = AccessTools.Field(typeof(TerrainGrid), nameof(TerrainGrid.TerrainDataLoaded));
    private static readonly FieldInfo terrainDataAboutToBeUnloadedField = AccessTools.Field(typeof(TerrainGrid), nameof(TerrainGrid.TerrainDataAboutToBeUnloaded));

    private TerrainGrid grid;
    // The extra cells: what the grid is keeping loaded for us.
    private readonly HashSet<GridCell> extra = new();
    private readonly HashSet<Vector2Int> extraCoords = new();
    // Our terrains, and the ones free for use.
    private readonly HashSet<Terrain> ours = new();
    private readonly List<Terrain> pool = new();
    private float nextCheck;
    private bool terrainChanged;

    public static ServerTerrainCoverage Instance { get; private set; }

    /// <summary>The active instance once it has found the grid, or null.</summary>
    public static ServerTerrainCoverage Active => Instance != null && Instance.grid != null ? Instance : null;

    /// <summary>The extra cells, for the grid's region checks.</summary>
    public bool IsExtra(Vector2Int coord) => extraCoords.Contains(coord);

    public bool IsExtra(GridCell cell) => extra.Contains(cell);

    public bool IsOurs(Terrain terrain) => terrain != null && ours.Contains(terrain);

    /// <summary>Called by ServerWorldCoverage on a multiplayer server.</summary>
    public static void Create(GameObject host)
    {
        if (Instance == null)
            Instance = host.AddComponent<ServerTerrainCoverage>();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private void Update()
    {
        if (grid == null)
        {
            grid = TerrainGrid.Instance;
            if (grid == null || !grid.IsInitialized)
                return;
        }

        if (Time.unscaledTime >= nextCheck && grid.worldUpdateCoro == null && grid.targetCoord.HasValue)
        {
            nextCheck = Time.unscaledTime + RecheckSeconds;
            ApplyCells(WantedCells());
        }

        // The grid loads extra cells but only displays its own box: display ours when loaded. A
        // few a frame: each builds a 513² heightfield collider on the main thread.
        int displayed = 0;
        foreach (GridCell cell in extra)
        {
            if (displayed >= MaxDisplaysPerFrame)
                break;
            if (!InPrimaryBox(cell.coord) && cell.GetStatus() == TerrainGrid.LoadingStatus.Loaded)
            {
                Display(cell);
                displayed++;
            }
        }

        // The grid clears finished loads from its list only on the way to moving its box, so
        // with only extra cells loading it never would, and would count as loading for good.
        if (grid.worldUpdateCoro == null)
            grid.inProgress.RemoveAll(cell => cell.terrainInfo.terrainData != null);

        if (terrainChanged)
        {
            terrainChanged = false;
            (terrainsMovedField?.GetValue(grid) as Action)?.Invoke();
        }
    }

    /// <summary>
    /// Whether a cell is in the box DV loads and displays itself, around its target point.
    /// </summary>
    public bool InPrimaryBox(Vector2Int coord) =>
        grid != null && grid.targetCoord.HasValue &&
        Math.Abs(coord.x - grid.targetCoord.Value.x) <= grid.loadingRingSize &&
        Math.Abs(coord.y - grid.targetCoord.Value.y) <= grid.loadingRingSize;

    private List<Vector2Int> WantedCells()
    {
        // Ordered by priority; the cap keeps the first.
        List<Vector2Int> wanted = new();

        if (CarSpawner.Instance?.AllCars != null)
        {
            foreach (TrainCar car in CarSpawner.Instance.AllCars)
            {
                if (car == null || !car.derailed)
                    continue;
                Vector3 p = car.transform.position;
                Vector2Int min = grid.ToGridCoords(p - new Vector3(DerailedCarMargin, 0f, DerailedCarMargin));
                Vector2Int max = grid.ToGridCoords(p + new Vector3(DerailedCarMargin, 0f, DerailedCarMargin));
                for (int x = min.x; x <= max.x; x++)
                    for (int y = min.y; y <= max.y; y++)
                        wanted.Add(new Vector2Int(x, y));
            }
        }

        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        List<Vector2Int> players = lifecycle.Server.GameplayPlayers
            .Where(player => !lifecycle.IsHost(player) && player.TrackingData.Position.HasValue)
            .Select(player => grid.ToGridCoords(player.WorldPosition))
            .ToList();
        // Ring by ring, so with too many cells every player keeps its nearest ones.
        for (int ring = 0; ring <= PlayerRing; ring++)
            foreach (Vector2Int center in players)
                for (int x = -ring; x <= ring; x++)
                    for (int y = -ring; y <= ring; y++)
                        if (Math.Max(Math.Abs(x), Math.Abs(y)) == ring)
                            wanted.Add(center + new Vector2Int(x, y));

        return wanted;
    }

    private void ApplyCells(List<Vector2Int> wanted)
    {
        HashSet<Vector2Int> next = new();
        foreach (Vector2Int coord in wanted)
        {
            if (next.Count >= MaxExtraCells)
                break;
            if (coord.x >= 0 && coord.y >= 0 && coord.x < grid.TerrainsPerAxis && coord.y < grid.TerrainsPerAxis)
                next.Add(coord);
        }
        if (next.SetEquals(extraCoords) && !extra.Any(cell => cell.GetStatus() == TerrainGrid.LoadingStatus.Unloaded && !grid.queue.Contains(cell)))
            return;

        // Cells no longer wanted: put our terrain back before the grid unloads them, or it would
        // go to DV's pool. One inside DV's box stays DV's and stays loaded.
        foreach (GridCell cell in extra.Where(cell => !next.Contains(cell.coord)).ToList())
        {
            if (InPrimaryBox(cell.coord))
                continue;
            if (cell.terrainInfo.terrainData != null)
                (terrainDataAboutToBeUnloadedField?.GetValue(null) as TerrainDataLoadedStateChangedDelegate)?.Invoke(cell.terrainInfo.terrainData, cell.coord);
            Hide(cell);
        }

        extraCoords.Clear();
        extraCoords.UnionWith(next);
        extra.Clear();
        foreach (Vector2Int coord in next)
            extra.Add(grid.grid[grid.ToIndex(coord)]);

        // Re-run the grid's cell selection: it unloads what neither it nor we keep, and queues
        // the rest for loading (TerrainGridPatch adds the extra cells to what it keeps).
        grid.targetCoord = null;
        grid.SetTargetCoord(grid.ToGridCoords(grid.trackingReference != null ? grid.trackingReference.position : Vector3.zero));
    }

    /// <summary>Displays a loaded extra cell with one of our terrains.</summary>
    private void Display(GridCell cell)
    {
        Terrain terrain = Take();
        terrain.transform.position = grid.ToWorldPosition(cell.coord);
        terrain.terrainData = cell.terrainInfo.terrainData;
        terrain.GetComponent<TerrainCollider>().terrainData = cell.terrainInfo.terrainData;
        terrain.gameObject.SetActive(true);
        cell.terrain = terrain;
        (terrainDataLoadedField?.GetValue(null) as TerrainDataLoadedStateChangedDelegate)?.Invoke(cell.terrainInfo.terrainData, cell.coord);
        terrainChanged = true;
    }

    /// <summary>Takes our terrain off a cell, leaving it loaded.</summary>
    public void Hide(GridCell cell)
    {
        if (!IsOurs(cell.terrain))
            return;
        Release(cell.terrain);
        cell.terrain = null;
        terrainChanged = true;
    }

    /// <summary>Returns one of our terrains to our pool.</summary>
    public void Release(Terrain terrain)
    {
        terrain.gameObject.SetActive(false);
        terrain.terrainData = null;
        terrain.GetComponent<TerrainCollider>().terrainData = null;
        pool.Add(terrain);
    }

    /// <summary>
    /// The grid moved its box off an extra cell it had displayed: swap its terrain for ours, so the
    /// cell stays loaded and DV's pool gets its terrain back.
    /// </summary>
    public void TakeOver(GridCell cell)
    {
        Terrain theirs = cell.terrain;
        if (theirs == null || IsOurs(theirs))
            return;
        // As the grid hides one: vegetation off first, then back to its pool.
        Behaviour vegetation = theirs.GetComponent("UnityTerrain") as Behaviour;
        if (vegetation != null)
            vegetation.enabled = false;
        cell.terrain = null;
        theirs.gameObject.SetActive(false);
        theirs.terrainData = null;
        theirs.GetComponent<TerrainCollider>().terrainData = null;
        grid.terrainPool.Add(theirs);
        Display(cell);
    }

    /// <summary>
    /// The grid's box reached an extra cell we display: give it back, so the grid displays it
    /// with one of its own terrains (which render, with vegetation).
    /// </summary>
    public void HandBack(GridCell cell) => Hide(cell);

    private Terrain Take()
    {
        if (pool.Count > 0)
        {
            Terrain pooled = pool[pool.Count - 1];
            pool.RemoveAt(pool.Count - 1);
            return pooled;
        }

        // Like the grid's own (TerrainGrid.Awake), without anything that renders.
        GameObject go = new($"server_terrain_{ours.Count}");
        go.SetActive(false);
        go.transform.SetParent(grid.transform);
        go.layer = grid.terrainLayer;
        Terrain terrain = go.AddComponent<Terrain>();
        go.AddComponent<TerrainCollider>();
        terrain.drawHeightmap = false;
        terrain.drawTreesAndFoliage = false;
        terrain.drawInstanced = false;
        terrain.heightmapPixelError = grid.pixelError;
        // Hazmat spills on far terrain: the grid's terrains get a detector when hazmat starts.
        if (grid.generatedTerrains.Count > 0 && grid.generatedTerrains[0].GetComponent<HazmatParticleDetector>() != null)
            go.AddComponent<HazmatParticleDetector>();
        ours.Add(terrain);
        return terrain;
    }
}
