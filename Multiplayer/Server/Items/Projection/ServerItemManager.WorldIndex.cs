using System.Collections.Generic;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.Projection;
using Multiplayer.Server.Gadgets;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;
using Multiplayer.Debugging.Protocol;
using UnityEngine;

namespace Multiplayer.Server.Items.Projection;

internal sealed partial class ServerItemManager
{
    // The host's index of world items by cell, which interest reads, and the hooks the authority
    // registry and the spatial and train-wake managers call into.

    public void RegisterHostWorldItem(NetworkedItem item)
    {
        if (!NetworkLifecycle.Instance.IsHost() || item == null || item.NetId == 0)
            return;
        RefreshHostWorldItem(item);
        TrainItemWake.ObserveCanonicalItem(item);
    }

    public void UnregisterHostWorldItem(NetworkedItem item)
    {
        if (item == null)
            return;
        if (HostCellByItem.TryGetValue(item, out WorldItemCellCoord cell) &&
            HostItemsByCell.TryGetValue(cell, out HashSet<NetworkedItem> items))
        {
            items.Remove(item);
            if (items.Count == 0)
                HostItemsByCell.Remove(cell);
        }
        HostCellByItem.Remove(item);
        HostNonSpatialItems.Remove(item);
        HostPlayerCells.Clear();
    }

    // At the item's own position, or the given absolute one: a transition is committed before its
    // Unity state is applied, so its record says where the item is going and the transform doesn't.
    private void RefreshHostWorldItem(NetworkedItem item, Vector3? absolutePosition = null)
    {
        if (item == null)
            return;
        if (HostCellByItem.TryGetValue(item, out WorldItemCellCoord oldCell) &&
            HostItemsByCell.TryGetValue(oldCell, out HashSet<NetworkedItem> oldItems))
        {
            oldItems.Remove(item);
            if (oldItems.Count == 0)
                HostItemsByCell.Remove(oldCell);
        }
        HostCellByItem.Remove(item);
        HostNonSpatialItems.Remove(item);

        if (ItemSaveLoad.IsRestorePending(item))
            return;

        if (LostAndFoundServer.Contains(item.NetId))
            return;
        // An installed gadget's inactive item isn't where its transform is (Server/Gadgets).
        // Committing it as installed indexes it again, as non-spatial.
        bool recorded = LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record);
        if ((!recorded || record.Placement != ItemPlacementKind.Installed) &&
            GadgetItemTransitions.TryCommitIfInstalled(item))
            return;
        if (recorded)
        {
            if (record.Placement == ItemPlacementKind.Destroyed)
                return;
            if (record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory or
                ItemPlacementKind.Container or ItemPlacementKind.Installed or
                ItemPlacementKind.TrainInterior or ItemPlacementKind.Attached or ItemPlacementKind.SnappedAttachment or
                ItemPlacementKind.Magazine)
            {
                HostNonSpatialItems.Add(item);
                HostPlayerCells.Clear();
                return;
            }
            if (HasOwnerRecallClaim(record))
                HostNonSpatialItems.Add(item);
        }

        WorldItemCellCoord cell = WorldItemCellCoord.FromAbsolute(
            absolutePosition ?? item.transform.position - WorldMover.currentMove);
        if (!HostItemsByCell.TryGetValue(cell, out HashSet<NetworkedItem> items))
            HostItemsByCell[cell] = items = new HashSet<NetworkedItem>();
        items.Add(item);
        HostCellByItem[item] = cell;
        // Re-evaluate the small per-player 3x3 union on the next tick. This is O(players), not a
        // rescan of the world catalogue, and handles a thrown item crossing a cell boundary.
        HostPlayerCells.Clear();
    }

    public void RefreshHostSpatialPosition(NetworkedItem item, Vector3 absolutePosition,
        ItemWorldParentKind parentKind)
    {
        if (!NetworkLifecycle.Instance.IsHost() || item == null)
            return;
        bool hadPreviousCell = HostCellByItem.TryGetValue(item, out WorldItemCellCoord previous);
        // On the ground or a shelf it lies in its cell (SH2, OI-SYNC-04); in a car it follows the car.
        bool byCell = WorldItemIndexing.IndexedByCell(parentKind switch
        {
            ItemWorldParentKind.StaticParent => LivePlacement.StaticParent,
            ItemWorldParentKind.TrainInterior => LivePlacement.TrainInterior,
            _ => LivePlacement.World
        });
        if (byCell)
        {
            WorldItemCellCoord unchanged = WorldItemCellCoord.FromAbsolute(absolutePosition);
            if (hadPreviousCell && previous.Equals(unchanged))
                return;
        }
        else if (!hadPreviousCell && HostNonSpatialItems.Contains(item))
            return;

        if (hadPreviousCell &&
            HostItemsByCell.TryGetValue(previous, out HashSet<NetworkedItem> previousItems))
        {
            previousItems.Remove(item);
            if (previousItems.Count == 0) HostItemsByCell.Remove(previous);
        }
        HostCellByItem.Remove(item);
        HostNonSpatialItems.Remove(item);
        if (!byCell)
        {
            HostNonSpatialItems.Add(item);
            HostPlayerCells.Clear();
            DebugRuntime.Publish("item-spatial", "item.spatial-cell-changed", DebugRuntimeSide.Server,
                entityType: "Item", entityId: item.NetId.ToString(), data: new()
                {
                    ["previousCell"] = hadPreviousCell ? previous.ToString() : string.Empty,
                    ["currentCell"] = string.Empty,
                    ["worldParentKind"] = parentKind.ToString()
                });
            return;
        }
        WorldItemCellCoord cell = WorldItemCellCoord.FromAbsolute(absolutePosition);
        HostCellByItem[item] = cell;
        if (!HostItemsByCell.TryGetValue(cell, out HashSet<NetworkedItem> items))
            HostItemsByCell[cell] = items = new HashSet<NetworkedItem>();
        items.Add(item);
        HostPlayerCells.Clear();
        DebugRuntime.Publish("item-spatial", "item.spatial-cell-changed", DebugRuntimeSide.Server,
            entityType: "Item", entityId: item.NetId.ToString(), data: new()
            {
                ["previousCell"] = hadPreviousCell ? previous.ToString() : string.Empty,
                ["currentCell"] = cell.ToString(),
                ["worldParentKind"] = parentKind.ToString()
            });
    }

    internal void OnAuthoritativeItemTransition(NetworkedItem item, ItemUpdate snapshot,
        LiveItems.LiveItem record, ServerPlayer actor, ItemPlacementKind previousPlacement,
        bool appliesPlacement)
    {
        // Every committed placement is indexed in the same operation, by the record's pose. One left
        // to its caller was missed: an item moved from a car interior into the world stayed in no
        // cell until a spatial sample came, and every player's interest retired it meanwhile and
        // re-created it at rest, ending a thrown one's flight.
        if (appliesPlacement && NetworkLifecycle.Instance.IsHost())
            RefreshHostWorldItem(item, record.Position);
        TrainItemWake.OnAuthoritativeTransition(item, record, previousPlacement, appliesPlacement);
        HostSpatial.OnAuthoritativeTransitionCommitted(item, snapshot, record, actor, previousPlacement,
            appliesPlacement);
        Replenishment?.OnAuthoritativeTransition(item, record, previousPlacement, appliesPlacement);
    }

    internal void ReceiveItemSpatialSample(ItemSpatialStateData state, ServerPlayer sender) =>
        HostSpatial.ReceiveClientSample(state, sender);

    internal void ReceiveItemSpatialSettlement(ItemSpatialStateData state, ServerPlayer sender) =>
        HostSpatial.ReceiveClientSettlement(state, sender);


    internal void ReceiveItemTrainWakeWitness(ServerboundItemTrainWakeWitnessPacket packet,
        ServerPlayer sender) => TrainItemWake.ReceiveImpactWitness(packet, sender);

    internal bool ResetAuthoredItemToBaseline(NetworkedItem item,
        LiveItems.LiveItem record, string reason) =>
        HostSpatial.ResetAuthoredItemToBaseline(item, record, reason);
}
