using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Physics;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.Physics;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Trains;
using UnityEngine;

namespace Multiplayer.Server.Items.Physics;

/// <summary>
/// The server's half of the fall rescue (docs/design/items/physics.md, "Fall rescue"; the shared
/// half is <see cref="ItemFallRescue"/>). It checks every loose item wherever it has terrain
/// loaded, on the ground, on a shelf or in a car, takes clients' reports of items under theirs,
/// and puts each back as a committed change (<see cref="ServerItemSpatial.CommitServerPose"/>):
/// a new revision every game is sent. A train item goes back into its car (OI-PHY-11, F38).
/// </summary>
internal sealed class FallRescueServer
{
    // Where an item last lay safe: absolute, or in its car's frame when it rode one.
    private readonly struct SafePose
    {
        public readonly ushort CarNetId;
        public readonly Vector3 Point;

        public SafePose(ushort carNetId, Vector3 point)
        {
            CarNetId = carNetId;
            Point = point;
        }
    }

    private readonly ServerItemManager manager;
    private readonly Dictionary<NetworkedItem, SafePose> lastSafe = new();
    private float nextScanAt;

    internal FallRescueServer(ServerItemManager manager) => this.manager = manager;

    internal void Clear()
    {
        lastSafe.Clear();
        nextScanAt = 0f;
    }

    internal void Run()
    {
        if (Time.unscaledTime < nextScanAt)
            return;
        nextScanAt = Time.unscaledTime + ItemFallRescue.RescueScanInterval;
        Scan();
    }

    private void Scan()
    {
        foreach (NetworkedItem item in lastSafe.Keys.Where(item => item == null).ToList())
            lastSafe.Remove(item);

        foreach (NetworkedItem item in NetworkedItem.GetAll())
        {
            if (!IsLoose(item, out LiveItems.LiveItem record))
            {
                // Held items are above ground with their holder; remember that as a fallback.
                if (item != null && item.NetId != 0 && item.gameObject.activeInHierarchy &&
                    LiveItems.TryGet(item.NetId, out record) &&
                    record.Placement == ItemPlacementKind.PlayerHand &&
                    ItemFallRescue.BelowTerrain(item.transform.position, out _) is float heldDepth && heldDepth < 0f)
                    lastSafe[item] = new SafePose(0, item.transform.position - WorldMover.currentMove);
                continue;
            }

            float? depth = ItemFallRescue.BelowTerrain(item.transform.position, out float surfaceY);
            if (depth == null)
                continue;
            if (depth.Value > ItemFallRescue.BelowTerrainTolerance)
            {
                if (ItemFallRescue.HasFloorUnder(item))
                    continue;   // in a tunnel
                Rescue(item, record, surfaceY, "host-terrain");
                continue;
            }

            Rigidbody rb = item.Item?.ItemRigidbody;
            if (depth.Value <= 0.2f && (rb == null || rb.isKinematic ||
                                        rb.velocity.sqrMagnitude < ItemFallRescue.RestSpeed * ItemFallRescue.RestSpeed))
                lastSafe[item] = SafePoseOf(item, record);
        }
    }

    /// <summary>A client saw this item under its terrain (surface height absolute).</summary>
    internal void HandleBelowMapReport(ServerPlayer reporter, ushort netId, float surfaceAbsoluteY)
    {
        if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || !IsLoose(item, out LiveItems.LiveItem record))
            return;
        // Only act if the authoritative copy is under that surface too; if just the client's copy
        // fell, convergence already puts it back to the host's pose.
        float hostAbsoluteY = item.transform.position.y - WorldMover.currentMove.y;
        if (hostAbsoluteY > surfaceAbsoluteY - ItemFallRescue.BelowTerrainTolerance)
            return;
        // The client may have no floor loaded where the host does (a tunnel's): it's in the tunnel.
        if (ItemFallRescue.HasFloorUnder(item))
            return;
        Rescue(item, record, surfaceAbsoluteY, "client-report:P" + reporter.PlayerId);
    }

    // On the ground, on a shelf or in a car, and nobody's hand on it (ItemLeaseRules.IsSimulatable).
    private static bool IsLoose(NetworkedItem item, out LiveItems.LiveItem record)
    {
        record = null;
        return item != null && item.NetId != 0 && item.gameObject.activeInHierarchy && item.Item != null &&
               !item.Item.IsGrabbed() &&
               LiveItems.TryGet(item.NetId, out record) &&
               ItemLeaseRules.IsSimulatable((Core.Items.LiveState.LivePlacement)(byte)record.Placement) &&
               !LostAndFoundServer.Contains(item.NetId) &&
               !ItemSaveLoad.IsRestorePending(item);
    }

    private static SafePose SafePoseOf(NetworkedItem item, LiveItems.LiveItem record)
    {
        if (record.Placement == ItemPlacementKind.TrainInterior && Frame(record.WorldParentNetId) is { } frame)
            return new SafePose(record.WorldParentNetId, frame.InverseTransformPoint(item.transform.position));
        return new SafePose(0, item.transform.position - WorldMover.currentMove);
    }

    private static Transform Frame(ushort carNetId) =>
        NetworkedTrainCar.TryGet(carNetId, out TrainCar car) && car != null ? car.interior ?? car.transform : null;

    private void Rescue(NetworkedItem item, LiveItems.LiveItem record, float surfaceAbsoluteY, string source)
    {
        Vector3 fallenAt = item.transform.position - WorldMover.currentMove;
        Vector3 target;
        bool known = lastSafe.TryGetValue(item, out SafePose safe);
        if (record.Placement == ItemPlacementKind.TrainInterior && Frame(record.WorldParentNetId) is { } frame)
        {
            // Back into its car: where it last lay there, or above the car's floor.
            Vector3 local = known && safe.CarNetId == record.WorldParentNetId
                ? safe.Point + Vector3.up * 0.1f
                : Vector3.up * 1.5f;
            target = frame.TransformPoint(local) - WorldMover.currentMove;
        }
        else if (known && safe.CarNetId == 0 &&
                 new Vector2(safe.Point.x - fallenAt.x, safe.Point.z - fallenAt.z).sqrMagnitude < 50f * 50f)
            target = safe.Point + Vector3.up * 0.1f;
        else
            target = new Vector3(fallenAt.x, surfaceAbsoluteY + 0.5f, fallenAt.z);

        // An item under the terrain is out of the map whether or not it is mid-flight: a physics
        // lease on it would never settle (it bobs on the water down there), so the put ends it.
        PutAt(item, record, target, ItemChangeLog.Move(ItemMoveReason.RescuedFromBelowMap,
            detail: $"from {fallenAt} to {target}, {source}"), "fell-through-map");
    }

    /// <summary>
    /// Puts a loose item at a position (absolute), at rest, as a committed change everyone who
    /// has it is sent at once: a fall rescue, a refused bin (OI-VAN-02).
    /// </summary>
    internal void PutAt(NetworkedItem item, LiveItems.LiveItem record, Vector3 target,
        ChangeCause<ItemMoveReason> moved, string leaseReason = "put-by-server")
    {
        Rigidbody rb = item.Item.ItemRigidbody;
        if (rb != null)
        {
            if (!rb.isKinematic)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            rb.position = target + WorldMover.currentMove;
        }
        item.transform.position = target + WorldMover.currentMove;
        manager.HostSpatial.CommitServerPose(item, record, moved, leaseReason);
    }
}
