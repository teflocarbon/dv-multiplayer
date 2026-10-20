using DV;
using UnityEngine;
using Multiplayer.Shared.Session;
using Multiplayer.Server.Session;

namespace Multiplayer.Server.Coverage;

/// <summary>
/// Keeps the server's world solid where its cars can derail, not only around its local player.
/// DV loads scenery around one point, so a car that derailed far from the host's player had
/// nothing under it and DV froze it in mid-air (DerailedTrainCarIsKinematicHandler).
///
/// - Every Near tile stays loaded on the server. They hold the ballast colliders along all the
///   track, bridge decks and tunnel portals. Measured on a host: about 76 MB, 36 s of loading in
///   the background, no frame-time cost.
/// - A derailed car may move where real ground is under both its ends, not only where DV has its
///   whole cell (Near and Far tiles and terrain) loaded. It's checked a few times a second, so a
///   car that slides off the covered ground is frozen again rather than falling.
/// - Terrain and Far scenery tiles are loaded around every remote player and every derailed car
///   (ServerTerrainCoverage, ServerSceneryCoverage), so that freeze is only until they arrive.
///
/// Multiplayer servers only, player host or dedicated; single player stays vanilla.
/// See docs/design/multi-anchor-streaming.md and docs/design/server-player-split.md.
/// </summary>
public sealed class ServerWorldCoverage : MonoBehaviour
{
    // The map is 16 Near tiles a side, so this covers it from any tile.
    private const int WholeMapTiles = 16;
    private const float RecheckSeconds = 0.25f;
    // A derailed car's ends must have static ground within this far below them.
    private const float GroundProbeAbove = 1.5f;
    private const float GroundProbeBelow = 4f;

    private static readonly RaycastHit[] hits = new RaycastHit[16];
    private float nextCheck;

    public static ServerWorldCoverage Instance { get; private set; }

    /// <summary>Called by the server once its world has loaded.</summary>
    public static void Create()
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (Instance != null || !lifecycle.IsHost() || NetworkServer.Instance.IsSinglePlayer)
            return;
        // In the game scene, so it goes with the world on unload.
        Instance = new GameObject("[server world coverage]").AddComponent<ServerWorldCoverage>();
    }

    private void Start()
    {
        LoadEveryNearTile();
        // Terrain and Far scenery tiles (platforms, buildings, tunnel walls) around every remote
        // player and derailed car.
        ServerTerrainCoverage.Create(gameObject);
        ServerSceneryCoverage.Create(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private static void LoadEveryNearTile()
    {
        Streamer near = WorldStreamingInit.Instance != null ? WorldStreamingInit.Instance.nearScenesStreamer : null;
        if (near == null)
        {
            Multiplayer.LogWarning("Server world coverage: no Near streamer; Near tiles stay loaded around the local player only");
            return;
        }
        near.loadingRange = new Vector3(WholeMapTiles, near.loadingRange.y, WholeMapTiles);
        near.deloadingRange = new Vector3(WholeMapTiles + 1, near.deloadingRange.y, WholeMapTiles + 1);
        // The streamer reloads at the new range on its next position check, which only acts
        // when the tracked tile changes; forget the tile so it does.
        near.xPos = int.MinValue;
        Multiplayer.Log("Server world coverage: loading every Near tile (track ballast, bridge decks, tunnel portals)");
    }

    private void Update()
    {
        if (Time.unscaledTime < nextCheck)
            return;
        nextCheck = Time.unscaledTime + RecheckSeconds;
        if (CarSpawner.Instance?.AllCars == null)
            return;

        foreach (TrainCar car in CarSpawner.Instance.AllCars)
            if (car != null && car.derailed)
                UpdateDerailedKinematic(car);
    }

    /// <summary>A derailed car is kinematic unless it <see cref="MayMove"/>.</summary>
    public static void UpdateDerailedKinematic(TrainCar car)
    {
        if (car.rb == null)
            return;
        // A pause makes every body kinematic and restores each one's own state after; leave it be.
        PausePhysicsHandler pause = PausePhysicsHandler.Instance;
        if (pause != null && pause.PhysicsHandlingInProcess)
            return;
        // Only write isKinematic when it changes: the write itself disturbs the body.
        bool kinematic = !MayMove(car);
        if (car.rb.isKinematic != kinematic)
            car.rb.isKinematic = kinematic;
    }

    /// <summary>
    /// Whether a derailed car may be simulated here: DV has its whole cell loaded, or there is static
    /// ground (Near tiles, terrain, any world geometry) under both its ends.
    /// </summary>
    public static bool MayMove(TrainCar car)
    {
        WorldStreamingInit streaming = WorldStreamingInit.Instance;
        if (streaming == null || streaming.IsSceneAndTerrainCellLoaded(car.transform.position))
            return true;
        Bogie[] bogies = car.Bogies;
        if (bogies == null || bogies.Length == 0)
            return HasGroundBelow(car.transform.position);
        foreach (Bogie bogie in bogies)
            if (bogie == null || !HasGroundBelow(bogie.transform.position))
                return false;
        return true;
    }

    // Static world geometry under a point: anything without a rigidbody, so not a car or an item.
    private static bool HasGroundBelow(Vector3 point)
    {
        int count = Physics.RaycastNonAlloc(point + Vector3.up * GroundProbeAbove, Vector3.down, hits,
            GroundProbeAbove + GroundProbeBelow, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
            if (hits[i].collider != null && hits[i].collider.attachedRigidbody == null)
                return true;
        return false;
    }
}
