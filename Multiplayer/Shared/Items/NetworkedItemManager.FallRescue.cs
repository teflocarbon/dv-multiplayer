using UnityEngine;

namespace Multiplayer.Shared.Items;

public partial class NetworkedItemManager
{
    // Rescue for items that fall out of the map. DV's terrain collider can be passed through
    // (seen with a steep throw onto the Steel Mill office ground, even with continuous collision
    // detection); the item then drops to the sea-level water volume under the terrain and floats
    // there, out of reach. Vanilla only respawns items hundreds of metres from both the player and
    // their spawn, so it never recovers these. Put them back where they were last safely above
    // ground.
    //
    // The host checks wherever it has terrain loaded (ServerItemManager.FallRescue.cs). Clients
    // report items near them that sink below their terrain, since the host usually has no terrain
    // loaded around a distant player (ClientItemManager.FallRescue.cs). What both use is here.

    internal const float BelowTerrainTolerance = 1f;    // metres under the surface before rescue
    internal const float RescueScanInterval = 1f;
    internal const float RestSpeed = 0.5f;              // m/s: settled enough to remember as safe

    /// <summary>Metres below the terrain surface (positive = underground), or null when no terrain
    /// is loaded under the position on this peer. Also returns the surface height (absolute).</summary>
    internal static float? BelowTerrain(Vector3 worldPosition, out float surfaceAbsoluteY)
    {
        surfaceAbsoluteY = 0f;
        foreach (Terrain terrain in Terrain.activeTerrains)
        {
            if (terrain == null || terrain.terrainData == null)
                continue;
            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            if (worldPosition.x < origin.x || worldPosition.z < origin.z ||
                worldPosition.x > origin.x + size.x || worldPosition.z > origin.z + size.z)
                continue;
            float surfaceWorldY = terrain.SampleHeight(worldPosition) + origin.y;
            surfaceAbsoluteY = surfaceWorldY - WorldMover.currentMove.y;
            return surfaceWorldY - worldPosition.y;
        }
        return null;
    }
}
