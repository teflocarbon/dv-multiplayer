using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Core.Diagnostics;
using UnityEngine;

namespace Multiplayer.Server.Items.Projection;

internal sealed partial class ServerItemManager
{
    // The host's half of the fall rescue (NetworkedItemManager.FallRescue.cs): it checks wherever
    // it has terrain loaded, takes clients' reports of items under theirs, and puts the items back.

    private float nextRescueScanAt;
    private readonly Dictionary<NetworkedItem, Vector3> lastSafeAbsolutePosition = new();

    private void RunFallRescue()
    {
        if (Time.unscaledTime < nextRescueScanAt)
            return;
        nextRescueScanAt = Time.unscaledTime + NetworkedItemManager.RescueScanInterval;
        HostScanForFallenItems();
    }

    private void HostScanForFallenItems()
    {
        foreach (NetworkedItem item in lastSafeAbsolutePosition.Keys.Where(item => item == null).ToList())
            lastSafeAbsolutePosition.Remove(item);

        foreach (NetworkedItem item in NetworkedItem.GetAll())
        {
            if (!IsWorldPhysicsItem(item, out LiveItems.LiveItem record))
            {
                // Held items are above ground with their holder; remember that as a fallback.
                if (item != null && item.NetId != 0 && item.gameObject.activeInHierarchy &&
                    LiveItems.TryGet(item.NetId, out record) &&
                    record.Placement == ItemPlacementKind.PlayerHand &&
                    NetworkedItemManager.BelowTerrain(item.transform.position, out _) is float heldDepth && heldDepth < 0f)
                    lastSafeAbsolutePosition[item] = item.transform.position - WorldMover.currentMove;
                continue;
            }

            float? depth = NetworkedItemManager.BelowTerrain(item.transform.position, out float surfaceY);
            if (depth == null)
                continue;
            if (depth.Value > NetworkedItemManager.BelowTerrainTolerance)
            {
                if (NetworkedItemManager.HasFloorUnder(item))
                    continue;   // in a tunnel
                RescueFallenItem(item, record, surfaceY, "host-terrain");
                continue;
            }

            Rigidbody rb = item.Item?.ItemRigidbody;
            if (depth.Value <= 0.2f && (rb == null || rb.isKinematic || rb.velocity.sqrMagnitude < NetworkedItemManager.RestSpeed * NetworkedItemManager.RestSpeed))
                lastSafeAbsolutePosition[item] = item.transform.position - WorldMover.currentMove;
        }
    }

    /// <summary>Host: a client saw this item under its terrain (surface height absolute).</summary>
    internal void HandleBelowMapReport(ServerPlayer reporter, ushort netId, float surfaceAbsoluteY)
    {
        if (!NetworkedItem.TryGet(netId, out NetworkedItem item) ||
            !IsWorldPhysicsItem(item, out LiveItems.LiveItem record))
            return;
        // Only act if the authoritative copy is under that surface too; if just the client's copy
        // fell, convergence already puts it back to the host's pose.
        float hostAbsoluteY = item.transform.position.y - WorldMover.currentMove.y;
        if (hostAbsoluteY > surfaceAbsoluteY - NetworkedItemManager.BelowTerrainTolerance)
            return;
        // The client may have no floor loaded where the host does (a tunnel's): it's in the tunnel.
        if (NetworkedItemManager.HasFloorUnder(item))
            return;
        RescueFallenItem(item, record, surfaceAbsoluteY, "client-report:P" + reporter.PlayerId);
    }

    private static bool IsWorldPhysicsItem(NetworkedItem item, out LiveItems.LiveItem record)
    {
        record = null;
        return item != null && item.NetId != 0 && item.gameObject.activeInHierarchy && item.Item != null &&
               !item.Item.IsGrabbed() &&
               LiveItems.TryGet(item.NetId, out record) &&
               record.Placement == ItemPlacementKind.World &&
               !LostAndFoundServer.Contains(item.NetId) &&
               !ItemSaveLoad.IsRestorePending(item);
    }

    private void RescueFallenItem(NetworkedItem item, LiveItems.LiveItem record,
        float surfaceAbsoluteY, string source)
    {
        // An item under the terrain is out of the map whether or not it is mid-flight: a physics
        // lease on it would never settle (it bobs on the water down there), so end it first.
        if (shell.HasActiveSpatialState(item.NetId))
            HostSpatial.RevokeLease(item.NetId, "fell-through-map");

        Vector3 fallenAt = item.transform.position - WorldMover.currentMove;
        Vector3 target;
        if (lastSafeAbsolutePosition.TryGetValue(item, out Vector3 safe) &&
            new Vector2(safe.x - fallenAt.x, safe.z - fallenAt.z).sqrMagnitude < 50f * 50f)
            target = safe + Vector3.up * 0.1f;
        else
            target = new Vector3(fallenAt.x, surfaceAbsoluteY + 0.5f, fallenAt.z);

        PutAt(item, record, target, ItemChangeLog.Move(ItemMoveReason.RescuedFromBelowMap,
            detail: $"from {fallenAt} to {target}, {source}"));
    }

    /// <summary>
    /// Host: puts a world item at a position (absolute), at rest, and tells everyone who has it now
    /// rather than waiting for the convergence pass: a fall rescue, a refused bin (OI-VAN-02).
    /// </summary>
    internal void PutAt(NetworkedItem item, LiveItems.LiveItem record, Vector3 target, ChangeCause<ItemMoveReason> moved)
    {
        // Whoever threw it no longer has its physics.
        if (shell.HasActiveSpatialState(item.NetId))
            HostSpatial.RevokeLease(item.NetId, "put-by-server");
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
        ItemChangeLog.RecordMove(item, record, moved);
        RefreshHostWorldItem(item);

        // Tell everyone who has it now rather than waiting for the convergence pass.
        foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
        {
            if (player.Peer == NetworkServer.Instance.SelfPeer || !player.KnownItems.ContainsKey(item))
                continue;
            if (TryCreateConvergenceSnapshot(item, out ItemUpdate snapshot))
            {
                snapshot.MoveCause = moved;
                NetworkServer.Instance.SendItemUpdate(snapshot, sendToPlayer: player);
            }
        }
    }}
