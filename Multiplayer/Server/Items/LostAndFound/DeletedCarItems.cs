using System;
using System.Collections.Generic;
using System.Linq;
using DV.CabControls;
using DV.Items;
using DV.Items.Snapping;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using UnityEngine;

namespace Multiplayer.Server.Items.LostAndFound;

/// <summary>
/// A car is about to be deleted (docs/design/items/lost-and-found.md, "A deleted car"): every item on
/// or in it is taken off it first, before vanilla puts it in the car pool and the records point at a
/// car that is gone. An owned item goes to its owner's Lost and Found, connected or not (OI-LF-09);
/// any other goes back into the world where it is, beside the track, as vanilla does (OI-LF-10),
/// never destroyed. That covers what lies in its interior, an end-of-train lantern on its coupler,
/// and an item snapped to anything on it (a hanger). An installed gadget comes off with vanilla's own
/// removal, and goes on from there (<see cref="InstalledOffDeletedCar"/>).
/// </summary>
public static class DeletedCarItems
{
    // The cars being deleted, by GUID, with their IDs for the log: on vanilla's slow path a car's
    // NetId is gone (OnDisable) by the time its gadgets come off, so a gadget's record names its car
    // by GUID (J2). From its prepare to its deletion.
    private static readonly Dictionary<string, string> deleting = new();
    private static readonly Dictionary<TrainCar, string> deletingGuids = new();

    /// <summary>Host: a car is being prepared for deleting; its items come off it now.</summary>
    public static void Collect(TrainCar car)
    {
        if (car == null || NetworkLifecycle.Instance?.IsHost() != true ||
            !NetworkedTrainCar.TryGetNetId(car, out ushort carNetId) || carNetId == 0)
            return;
        string guid = car.CarGUID;
        if (!string.IsNullOrEmpty(guid))
        {
            deleting[guid] = car.ID;
            deletingGuids[car] = guid;
        }
        // Snapped first: an unsnap gives a lantern back to the parent it had, which may be this car.
        foreach (NetworkedItem item in NetworkedItem.GetAll().Where(item => item != null && item.NetId != 0 &&
                     SnappedOn(item, car)).Distinct().ToList())
            TryTakeOff(item, car);
        foreach (ushort netId in LiveItems.GetAll()
                     .Where(record => record.Placement == ItemPlacementKind.TrainInterior && record.WorldParentNetId == carNetId ||
                                      record.Placement == ItemPlacementKind.Attached && record.AttachedCarNetId == carNetId)
                     .Select(record => record.NetId).ToList())
            if (NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null)
                TryTakeOff(item, car);
    }

    /// <summary>
    /// Host: vanilla took an installed gadget off a car (GadgetItemTransitions), whose record is now
    /// in the world at the gadget's pose. On a car being deleted, an owned one goes on to its owner's
    /// Lost and Found; any other stays where it came off.
    /// </summary>
    internal static void InstalledOffDeletedCar(NetworkedItem item, string carGuid)
    {
        if (item == null || string.IsNullOrEmpty(carGuid) || !deleting.TryGetValue(carGuid, out string carId) ||
            !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record) || record.Owner == Guid.Empty)
            return;
        bool collected;
        try
        {
            collected = LostAndFoundServer.Collect(item, LostItemReason.ContainerCleared, $"car {carId} deleted, it was installed on it");
        }
        catch (Exception exception)
        {
            // In vanilla's deletion, as TryTakeOff.
            Multiplayer.LogException($"Deleted car {carId}: its gadget {item.name} (netId {item.NetId}) failed to go to Lost and Found", exception);
            return;
        }
        if (!collected)
            Multiplayer.LogWarning($"Deleted car {carId}: Lost and Found refused its gadget {item.name} (netId {item.NetId}); " +
                                   "it stays where it came off");
    }

    /// <summary>Vanilla has deleted the car (or pooled it): its gadgets have come off.</summary>
    internal static void Deleted(TrainCar car)
    {
        if (car != null && deletingGuids.TryGetValue(car, out string guid))
        {
            deletingGuids.Remove(car);
            deleting.Remove(guid);
        }
    }

    internal static void Clear()
    {
        deleting.Clear();
        deletingGuids.Clear();
    }

    // Inside vanilla's deletion, where an exception would stop the rest of it (J5): each item's is
    // logged, and the next goes on.
    private static void TryTakeOff(NetworkedItem item, TrainCar car)
    {
        try
        {
            TakeOff(item, car);
        }
        catch (Exception exception)
        {
            Multiplayer.LogException($"Deleted car {car.ID}: taking {item.name} (netId {item.NetId}) off it failed", exception);
        }
    }

    // An item snapped to a point on this car: a lantern on its coupler, an item on a hanger it has.
    private static bool SnappedOn(NetworkedItem item, TrainCar car)
    {
        ItemSnapPointBase point = item.Item?.SnappableItem?.SnappedTo;
        return point != null && (point is ItemSnapPointCoupler coupler && coupler.Car == car ||
                                 TrainCar.Resolve(point.gameObject) == car);
    }

    // Off the car first (unsnapped, out of its interior), so the pool doesn't take it; then an owned
    // item to its owner, and any other into the world where it is.
    private static void TakeOff(NetworkedItem item, TrainCar car)
    {
        if (LostAndFoundServer.Contains(item.NetId))
            return;
        item.LeaveSnapPoint();
        item.transform.SetParent(WorldMover.OriginShiftParent, true);
        // Owned, or a booklet kept for the player it was issued to (OI-LF-17).
        bool owned = LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record) && LiveItems.KeeperOf(record) != Guid.Empty;
        bool attached = record?.Placement == ItemPlacementKind.Attached;
        if (owned && LostAndFoundServer.Collect(item, LostItemReason.ContainerCleared, $"car {car.ID} deleted"))
            return;
        if (owned)
            Multiplayer.LogWarning($"Deleted car {car.ID}: Lost and Found refused {item.name} (netId {item.NetId}); " +
                                   "it goes into the world where it is");
        // A refused collection off a coupler put it in the world already (LostAndFoundServer.RollBack).
        if (!(owned && attached && record.Placement == ItemPlacementKind.World))
            IntoWorld(item, record, $"car {car.ID} deleted, left beside the track");
    }

    /// <summary>
    /// Loose in the world at its pose: the server commits it and tells everyone, and physics takes it
    /// down (beside the track, off a deleted car). Nothing destroys it (LF2).
    /// </summary>
    internal static void IntoWorld(NetworkedItem item, LiveItems.LiveItem record, string detail)
    {
        ItemUpdate drop = item.CreateUpdateData(ItemUpdate.ItemUpdateType.FullSync);
        if (drop == null || record == null)
        {
            Multiplayer.LogWarning($"{item.name} (netId {item.NetId}) has no record ({detail}); it is left to vanilla");
            return;
        }
        drop.ItemState = ItemState.Dropped;
        drop.PlayerId = 0;
        drop.PlayerHand = PlayerHand.None;
        drop.ItemPosition = item.transform.position - WorldMover.currentMove;
        drop.ItemRotation = item.transform.rotation;
        drop.WorldParentKind = ItemWorldParentKind.World;
        drop.WorldParentNetId = 0;
        drop.WorldParentKey = string.Empty;
        drop.CarNetId = 0;
        drop.MagazineNetId = 0;
        drop.MagazineSlot = -1;
        drop.AuthorityRevision = record.Revision;
        if (!LiveItems.TryApplyServerTransition(item, drop, ItemTransitionReason.HostLocalState, true, out string rejection,
                ItemChangeLog.Move(ItemMoveReason.Detached, detail: detail)))
        {
            Multiplayer.LogWarning($"{item.name} (netId {item.NetId}) couldn't go into the world ({detail}): {rejection}");
            return;
        }
        item.ApplyServerCanonicalSnapshot(drop);
        ServerItemManager.Instance?.RegisterHostWorldItem(item);
        NetworkServer.Instance.SendItemUpdate(drop);
    }
}
