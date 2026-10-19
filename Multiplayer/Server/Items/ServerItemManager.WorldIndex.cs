using System.Collections.Generic;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;
using Multiplayer.Debugging.Protocol;
using UnityEngine;

namespace Multiplayer.Server.Items;

internal sealed partial class ServerItemManager
{
    // The host's index of world items by cell, which interest reads, and the hooks the authority
    // registry and the spatial and train-wake managers call into.

    public void RegisterHostWorldItem(NetworkedItem item)
    {
        if (!NetworkLifecycle.Instance.IsHost() || item == null || item.NetId == 0)
            return;
        RefreshHostWorldItem(item);
        shell.TrainItemWake?.ObserveCanonicalItem(item);
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

    private void RefreshHostWorldItem(NetworkedItem item)
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

        if (WorldItemPersistenceManager.IsRestorePending(item))
            return;

        if (NetworkedLostAndFoundManager.Contains(item.NetId))
            return;
        if (AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record))
        {
            if (record.Placement == ItemPlacementKind.Destroyed)
                return;
            if (record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory or
                ItemPlacementKind.Container or ItemPlacementKind.Installed or
                ItemPlacementKind.TrainInterior or ItemPlacementKind.Attached or ItemPlacementKind.SnappedAttachment)
            {
                HostNonSpatialItems.Add(item);
                HostPlayerCells.Clear();
                return;
            }
            if (HasOwnerRecallClaim(record))
                HostNonSpatialItems.Add(item);
        }

        WorldItemCellCoord cell = WorldItemCellCoord.FromAbsolute(item.transform.position - WorldMover.currentMove);
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
        if (parentKind == ItemWorldParentKind.World)
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
        if (parentKind != ItemWorldParentKind.World)
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

    internal void OnAuthoritativeItemTransition(NetworkedItem item, ItemUpdateData snapshot,
        AuthoritativeItemRegistry.Record record, ServerPlayer actor, ItemPlacementKind previousPlacement,
        bool appliesPlacement)
    {
        shell.TrainItemWake?.OnAuthoritativeTransition(item, record, previousPlacement, appliesPlacement);
        shell.Spatial?.OnAuthoritativeTransitionCommitted(item, snapshot, record, actor, previousPlacement,
            appliesPlacement);
        Replenishment?.OnAuthoritativeTransition(item, record, previousPlacement, appliesPlacement);
    }

    internal void ReceiveItemSpatialSample(ItemSpatialStateData state, ServerPlayer sender) =>
        shell.Spatial?.ReceiveClientSample(state, sender);

    internal void ReceiveItemSpatialSettlement(ItemSpatialStateData state, ServerPlayer sender) =>
        shell.Spatial?.ReceiveClientSettlement(state, sender);


    internal void ReceiveItemTrainWakeWitness(ServerboundItemTrainWakeWitnessPacket packet,
        ServerPlayer sender) => shell.TrainItemWake?.ReceiveImpactWitness(packet, sender);

    internal bool ResetAuthoredItemToBaseline(NetworkedItem item,
        AuthoritativeItemRegistry.Record record) =>
        shell.Spatial?.ResetAuthoredItemToBaseline(item, record) == true;
}
