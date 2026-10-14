using System.Collections.Generic;
using System.Linq;
using Multiplayer.Components.Networking.World.WorldItems;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using UnityEngine;

namespace Multiplayer.Components.Networking.World;

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

    private const float BelowTerrainTolerance = 1f;     // metres under the surface before rescue
    private const float RescueScanInterval = 1f;
    private const float ClientReportCooldown = 5f;
    private const float ClientScanRadius = 200f;
    private const float RestSpeed = 0.5f;               // m/s: settled enough to remember as safe

    private float nextRescueScanAt;
    private readonly Dictionary<NetworkedItem, Vector3> lastSafeAbsolutePosition = new();
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

    private void RunFallRescue()
    {
        if (Time.unscaledTime < nextRescueScanAt)
            return;
        nextRescueScanAt = Time.unscaledTime + RescueScanInterval;

        if (NetworkLifecycle.Instance.IsHost())
            HostScanForFallenItems();
        else
            ClientScanForFallenItems();
    }

    private void HostScanForFallenItems()
    {
        foreach (NetworkedItem item in lastSafeAbsolutePosition.Keys.Where(item => item == null).ToList())
            lastSafeAbsolutePosition.Remove(item);

        foreach (NetworkedItem item in NetworkedItem.GetAll())
        {
            if (!IsWorldPhysicsItem(item, out AuthoritativeItemRegistry.Record record))
            {
                // Held items are above ground with their holder; remember that as a fallback.
                if (item != null && item.NetId != 0 && item.gameObject.activeInHierarchy &&
                    AuthoritativeItemRegistry.TryGet(item.NetId, out record) &&
                    record.Placement == ItemPlacementKind.PlayerHand &&
                    BelowTerrain(item.transform.position, out _) is float heldDepth && heldDepth < 0f)
                    lastSafeAbsolutePosition[item] = item.transform.position - WorldMover.currentMove;
                continue;
            }

            float? depth = BelowTerrain(item.transform.position, out float surfaceY);
            if (depth == null)
                continue;
            if (depth.Value > BelowTerrainTolerance)
            {
                RescueFallenItem(item, record, surfaceY, "host-terrain");
                continue;
            }

            Rigidbody rb = item.Item?.ItemRigidbody;
            if (depth.Value <= 0.2f && (rb == null || rb.isKinematic || rb.velocity.sqrMagnitude < RestSpeed * RestSpeed))
                lastSafeAbsolutePosition[item] = item.transform.position - WorldMover.currentMove;
        }
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

    /// <summary>Host: a client saw this item under its terrain (surface height absolute).</summary>
    internal void Server_HandleBelowMapReport(ServerPlayer reporter, ushort netId, float surfaceAbsoluteY)
    {
        if (!NetworkedItem.TryGet(netId, out NetworkedItem item) ||
            !IsWorldPhysicsItem(item, out AuthoritativeItemRegistry.Record record))
            return;
        // Only act if the authoritative copy is under that surface too; if just the client's copy
        // fell, convergence already puts it back to the host's pose.
        float hostAbsoluteY = item.transform.position.y - WorldMover.currentMove.y;
        if (hostAbsoluteY > surfaceAbsoluteY - BelowTerrainTolerance)
            return;
        RescueFallenItem(item, record, surfaceAbsoluteY, "client-report:P" + reporter.PlayerId);
    }

    private static bool IsWorldPhysicsItem(NetworkedItem item, out AuthoritativeItemRegistry.Record record)
    {
        record = null;
        return item != null && item.NetId != 0 && item.gameObject.activeInHierarchy && item.Item != null &&
               !item.Item.IsGrabbed() &&
               AuthoritativeItemRegistry.TryGet(item.NetId, out record) &&
               record.Placement == ItemPlacementKind.World &&
               !NetworkedLostAndFoundManager.Contains(item.NetId) &&
               !WorldItemPersistenceManager.IsRestorePending(item);
    }

    private void RescueFallenItem(NetworkedItem item, AuthoritativeItemRegistry.Record record,
        float surfaceAbsoluteY, string source)
    {
        // An item under the terrain is out of the map whether or not it is mid-flight: a physics
        // lease on it would never settle (it bobs on the water down there), so end it first.
        if (HasActiveSpatialState(item.NetId))
            Spatial?.RevokeLease(item.NetId, "fell-through-map");

        Vector3 fallenAt = item.transform.position - WorldMover.currentMove;
        Vector3 target;
        if (lastSafeAbsolutePosition.TryGetValue(item, out Vector3 safe) &&
            new Vector2(safe.x - fallenAt.x, safe.z - fallenAt.z).sqrMagnitude < 50f * 50f)
            target = safe + Vector3.up * 0.1f;
        else
            target = new Vector3(fallenAt.x, surfaceAbsoluteY + 0.5f, fallenAt.z);

        Rigidbody rb = item.Item.ItemRigidbody;
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.position = target + WorldMover.currentMove;
        }
        item.transform.position = target + WorldMover.currentMove;
        record.Position = target;
        record.Rotation = item.transform.rotation;

        Multiplayer.Log($"Rescued item {item.NetId} ({item.name}) from below the map at {fallenAt} " +
                        $"to {target} ({source})");
        RefreshHostWorldItem(item);

        // Tell everyone who has it now rather than waiting for the convergence pass.
        foreach (ServerPlayer player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.Peer == NetworkLifecycle.Instance.Server.SelfPeer || !player.KnownItems.ContainsKey(item))
                continue;
            if (TryCreateConvergenceSnapshot(item, out ItemUpdateData snapshot))
                NetworkLifecycle.Instance.Server.SendItemUpdatePacket(snapshot, sendToPlayer: player);
        }
    }
}
