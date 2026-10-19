using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Coverage;

/// <summary>
/// Far scenery tiles on the server around every remote player and every derailed car, not only
/// around the one point DV streams them for. Far tiles (1024 m) hold the platforms, buildings,
/// yards, roads and tunnel walls; without them a car derailed far from that point went through a
/// platform or a tunnel wall on the server. (Near tiles, the track ballast, are all loaded already:
/// ServerWorldCoverage.)
///
/// DV's Far streamer loads a 5×5 box of tiles around its tracker and, after the tracker crosses a
/// tile, unloads what's outside a range of it. This queues the extra tiles itself, the way the
/// streamer's own SceneLoading does, and the streamer loads them in turn; StreamerPatch keeps its
/// unloading off the tiles still wanted here. Tiles stay until no anchor is within a tile more of
/// them than loads them, so walking along a tile edge doesn't reload it.
///
/// Multiplayer servers only. See docs/design/multi-anchor-streaming.md §1.4.
/// </summary>
public sealed class ServerSceneryCoverage : MonoBehaviour
{
    // DV's own Far box is 2 tiles each way; a remote player gets 1 (a 3 km box).
    private const int PlayerRange = 1;
    // A derailed car gets the tiles within this distance of it.
    private const float DerailedCarMargin = 64f;
    // A cap on extra Far tiles (the whole map is 256), nearest-to-an-anchor first.
    private const int MaxExtraTiles = 40;
    private const float RecheckSeconds = 1f;

    private Streamer far;
    // Tiles to load, and tiles to keep (loaded ones a little further out).
    private readonly HashSet<Vector2Int> load = new();
    private readonly HashSet<Vector2Int> keep = new();
    private float nextCheck;

    public static ServerSceneryCoverage Instance { get; private set; }

    /// <summary>Called by ServerWorldCoverage on a multiplayer server.</summary>
    public static void Create(GameObject host)
    {
        if (Instance == null)
            Instance = host.AddComponent<ServerSceneryCoverage>();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    /// <summary>Whether this is the streamer whose tiles are covered here.</summary>
    public bool Covers(Streamer streamer) => streamer != null && streamer == far;

    /// <summary>Whether a loaded tile is still wanted here, so the streamer mustn't unload it.</summary>
    public bool Keeps(SceneSplit scene) => keep.Contains(new Vector2Int(scene.posX, scene.posZ));

    private void Update()
    {
        if (far == null)
        {
            far = WorldStreamingInit.Instance != null ? WorldStreamingInit.Instance.farScenesStreamer : null;
            if (far == null || far.scenesArray == null)
                return;
        }
        if (Time.unscaledTime < nextCheck)
            return;
        nextCheck = Time.unscaledTime + RecheckSeconds;

        Collect();

        foreach (Vector2Int tile in load)
            Queue(tile);

        // Tiles nobody here keeps any more, outside the streamer's own range: let the streamer
        // unload them (StreamerPatch leaves the kept ones alone).
        if (far.loadedScenes.Any(scene => scene.sceneGo != null && !Keeps(scene) && OutsidePrimary(scene)))
            far.SceneUnloading();
    }

    private void Collect()
    {
        // Each anchor: the tiles it loads (range r) and keeps (range r + 1), in tile units.
        List<(Vector2 pos, float loadReach, float keepReach)> anchors = new();
        float size = far.sceneCollection.xSize;

        if (CarSpawner.Instance?.AllCars != null)
            foreach (TrainCar car in CarSpawner.Instance.AllCars)
                if (car != null && car.derailed)
                    anchors.Add((Tile(car.transform.position), DerailedCarMargin / size, 2f * DerailedCarMargin / size));

        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        foreach (var player in lifecycle.Server.GameplayPlayers)
            if (!lifecycle.IsHost(player) && player.TrackingData.Position.HasValue)
                anchors.Add((Tile(player.WorldPosition), PlayerRange + 0.5f, PlayerRange + 1.5f));

        load.Clear();
        keep.Clear();
        // Nearest to an anchor first, so the cap drops the outer tiles.
        foreach (var (tile, distance) in anchors
                     .SelectMany(a => TilesWithin(a.pos, a.keepReach).Select(t => (t, d: Distance(a.pos, t) - a.loadReach)))
                     .OrderBy(x => x.d))
        {
            if (keep.Count >= MaxExtraTiles)
                break;
            if (!far.scenesArray.ContainsKey(new[] { tile.x, 0, tile.y }))
                continue;
            keep.Add(tile);
            if (distance <= 0f)
                load.Add(tile);
        }
    }

    // The position in tile units, as the streamer computes tiles (without the origin shift).
    private Vector2 Tile(Vector3 worldPosition)
    {
        Vector3 p = worldPosition - far.currentMove;
        return new Vector2(p.x / far.sceneCollection.xSize, p.z / far.sceneCollection.zSize);
    }

    private static IEnumerable<Vector2Int> TilesWithin(Vector2 pos, float reach)
    {
        for (int x = Mathf.FloorToInt(pos.x - reach); x <= Mathf.FloorToInt(pos.x + reach); x++)
            for (int z = Mathf.FloorToInt(pos.y - reach); z <= Mathf.FloorToInt(pos.y + reach); z++)
                yield return new Vector2Int(x, z);
    }

    // How far a point is outside a tile, in tile units (0 inside it).
    private static float Distance(Vector2 pos, Vector2Int tile)
    {
        float dx = Math.Max(Math.Max(tile.x - pos.x, pos.x - (tile.x + 1)), 0f);
        float dz = Math.Max(Math.Max(tile.y - pos.y, pos.y - (tile.y + 1)), 0f);
        return Math.Max(dx, dz);
    }

    private bool OutsidePrimary(SceneSplit scene) =>
        Math.Abs(scene.posX - far.xPos) > (int)far.deloadingRange.x || Math.Abs(scene.posZ - far.zPos) > (int)far.deloadingRange.x;

    /// <summary>Queues a tile for loading, as Streamer.SceneLoading does (no looping, no limits).</summary>
    private void Queue(Vector2Int tile)
    {
        if (!far.scenesArray.TryGetValue(new[] { tile.x, 0, tile.y }, out SceneSplit scene) || scene.loaded)
            return;
        scene.loaded = true;
        scene.posXLimitMove = 0f;
        scene.xDeloadLimit = 0;
        scene.posYLimitMove = 0f;
        scene.yDeloadLimit = 0;
        scene.posZLimitMove = 0f;
        scene.zDeloadLimit = 0;
        far.scenesToLoad.Add(scene);
        far.loadedScenes.Add(scene);
    }
}
