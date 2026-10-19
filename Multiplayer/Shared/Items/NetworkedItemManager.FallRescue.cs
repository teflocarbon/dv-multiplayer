using System.Collections.Generic;
using System.Linq;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items;
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
    // The host checks wherever it has terrain loaded. Clients report items near them that sink
    // below their terrain, since the host usually has no terrain loaded around a distant player.

    internal const float BelowTerrainTolerance = 1f;    // metres under the surface before rescue
    internal const float RescueScanInterval = 1f;
    private const float ClientReportCooldown = 5f;
    private const float ClientScanRadius = 200f;
    internal const float RestSpeed = 0.5f;              // m/s: settled enough to remember as safe

    private float nextRescueScanAt;
    private readonly Dictionary<ushort, float> clientRescueReportedAt = new();

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

    // The client's scan; the host's is the server's (ServerItemManager.FallRescue.cs).
    private void RunFallRescue()
    {
        if (Time.unscaledTime < nextRescueScanAt)
            return;
        nextRescueScanAt = Time.unscaledTime + RescueScanInterval;

        ClientScanForFallenItems();
    }

    private void ClientScanForFallenItems()
    {
        if (PlayerManager.PlayerTransform == null)
            return;
        Vector3 player = PlayerManager.PlayerTransform.position;
        foreach (NetworkedItem item in NetworkedItem.GetAll())
        {
            if (item == null || item.NetId == 0 || !item.gameObject.activeInHierarchy ||
                item.Item == null || item.Item.IsGrabbed() ||
                (item.transform.position - player).sqrMagnitude > ClientScanRadius * ClientScanRadius)
                continue;
            if (item.GetItemState() is not (ItemState.Dropped or ItemState.Thrown))
                continue;
            if (BelowTerrain(item.transform.position, out float surfaceY) is not float depth ||
                depth <= BelowTerrainTolerance)
                continue;
            if (clientRescueReportedAt.TryGetValue(item.NetId, out float reportedAt) &&
                Time.unscaledTime - reportedAt < ClientReportCooldown)
                continue;
            clientRescueReportedAt[item.NetId] = Time.unscaledTime;
            NetworkLifecycle.Instance.Client.SendItemBelowMapReport(item.NetId, surfaceY);
        }
    }
}
