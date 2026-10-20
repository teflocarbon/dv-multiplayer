using DV.CabControls;
using UnityEngine;

namespace Multiplayer.Shared.Items;

public partial class NetworkedItemManager
{
    // Rescue for items that fall out of the map. DV's terrain collider can be passed through
    // (seen with a steep throw onto the Steel Mill office ground, even with continuous collision
    // detection); the item then drops to the sea-level water volume under the terrain and floats
    // there, out of reach. Vanilla only respawns items hundreds of metres from both the player and
    // their spawn, so it never recovers these. Put them back where they were last safely above
    // ground. Most of these never get here now: the terrain guard (NetworkedItemManager.TerrainGuard.cs)
    // puts an item that goes through the terrain back on it the same physics step. This is what's
    // left when the guard can't see it (no terrain loaded, a body it doesn't simulate).
    //
    // An item under the terrain with a floor under it (HasFloorUnder) is in a tunnel or on an
    // underground floor, and stays where it is.
    //
    // The host checks wherever it has terrain loaded (ServerItemManager.FallRescue.cs). Clients
    // report items near them that sink below their terrain, since the host usually has no terrain
    // loaded around a distant player (ClientItemManager.FallRescue.cs). What both use is here.

    internal const float BelowTerrainTolerance = 1f;    // metres under the surface before rescue
    internal const float RescueScanInterval = 1f;
    internal const float RestSpeed = 0.5f;              // m/s: settled enough to remember as safe
    internal const float UndergroundFloorReach = 3f;    // metres under an item to look for a floor

    /// <summary>Metres below the terrain surface (positive = underground), or null when no terrain
    /// is loaded under the position on this peer. Also returns the surface height (absolute).</summary>
    internal static float? BelowTerrain(Vector3 worldPosition, out float surfaceAbsoluteY)
    {
        surfaceAbsoluteY = 0f;
        if (TerrainUnder(worldPosition) is not Terrain terrain)
            return null;
        float surfaceWorldY = terrain.SampleHeight(worldPosition) + terrain.transform.position.y;
        surfaceAbsoluteY = surfaceWorldY - WorldMover.currentMove.y;
        return surfaceWorldY - worldPosition.y;
    }

    /// <summary>The loaded terrain whose area covers the position on this peer, if any.</summary>
    internal static Terrain TerrainUnder(Vector3 worldPosition)
    {
        foreach (Terrain terrain in Terrain.activeTerrains)
        {
            if (terrain == null || terrain.terrainData == null)
                continue;
            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            if (worldPosition.x < origin.x || worldPosition.z < origin.z ||
                worldPosition.x > origin.x + size.x || worldPosition.z > origin.z + size.z)
                continue;
            return terrain;
        }
        return null;
    }

    /// <summary>
    /// Something other than the terrain, the item and other items is under the item, within
    /// <see cref="UndergroundFloorReach"/>: it's on a tunnel's floor or another floor under the
    /// terrain, not falling through the void under it, so it's left there.
    /// </summary>
    internal static bool HasFloorUnder(NetworkedItem item)
    {
        Vector3 from = item.transform.position + Vector3.up * 0.5f;
        foreach (RaycastHit hit in Physics.RaycastAll(from, Vector3.down, UndergroundFloorReach + 0.5f,
                     Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider is TerrainCollider || hit.collider.transform.IsChildOf(item.transform) ||
                hit.collider.GetComponentInParent<ItemBase>() != null)
                continue;
            return true;
        }
        return false;
    }
}
