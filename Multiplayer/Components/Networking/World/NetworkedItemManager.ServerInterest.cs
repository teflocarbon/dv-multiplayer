using System.Collections.Generic;
using System.Linq;
using DV.Utils;
using UnityEngine;
using JetBrains.Annotations;
using Multiplayer.Networking.Data;
using Multiplayer.Components.Networking.World;
using System;
using Multiplayer.Utils;
using DV;
using DV.InventorySystem;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Items;
using Multiplayer.Core.Replication;
using DV.Booklets;
using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Networking.Data.Jobs;
using Multiplayer.Components.Networking.World.Containers;
using Multiplayer.Integrations.Inventory;
using Multiplayer.Integrations.Storage;
using Multiplayer.Components.Networking.World.WorldItems;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Components.Networking.Train;

namespace Multiplayer.Components.Networking.World;

public partial class NetworkedItemManager
{
    // Host recipient interest, known-item lifetimes, and outbound replication.

    private void UpdatePlayerItemLists()
    {
        foreach (var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;

            WorldItemCellCoord centre = WorldItemCellCoord.FromAbsolute(player.AbsoluteWorldPosition);
            HashSet<WorldItemCellCoord> desiredCells = WorldItemCellCoord.Neighbourhood(centre);
            bool cellsChanged = !HostPlayerCells.TryGetValue(player.PlayerId, out HashSet<WorldItemCellCoord> previousCells) ||
                                !previousCells.SetEquals(desiredCells);
            HashSet<NetworkedItem> desiredItems = new(HostNonSpatialItems.Where(item =>
                IsNonSpatialRelevantTo(item, player, desiredCells) &&
                (!item.IsSceneAuthored || player.WorldItemCatalogueAccepted)));
            foreach (WorldItemCellCoord cell in desiredCells)
            {
                if (HostItemsByCell.TryGetValue(cell, out HashSet<NetworkedItem> indexed))
                    desiredItems.UnionWith(indexed.Where(item => item != null &&
                        (!item.IsSceneAuthored || player.WorldItemCatalogueAccepted)));
            }
            bool membershipChanged = cellsChanged || player.NearbyItems.Count != desiredItems.Count ||
                                     player.NearbyItems.Keys.Any(item => !desiredItems.Contains(item));
            if (membershipChanged)
            {

                foreach (NetworkedItem leaving in player.NearbyItems.Keys
                             .Where(item => item == null || !desiredItems.Contains(item)).ToArray())
                {
                    player.NearbyItems.Remove(leaving);
                    if (leaving != null && player.Peer != NetworkLifecycle.Instance.Server.SelfPeer &&
                        player.KnownItems.Remove(leaving))
                    {
                        // Authority ends when host interest ends. The later client acknowledgement
                        // is observability, not a lease that can extend mutation permission.
                        player.AcknowledgedWorldItems.Remove(leaving.NetId);
                        ItemUpdateData retirement = leaving.CreateUpdateData(ItemUpdateData.ItemUpdateType.Destroy);
                        if (retirement != null)
                        {
                            retirement.TransitionReason = ItemTransitionReason.InterestRetirement;
                            NetworkLifecycle.Instance.Server.SendItemUpdatePacket(retirement, player);
                        }
                    }
                    if (leaving != null)
                        TraceItem("item.cell-interest-leave", leaving,
                            new() { ["playerId"] = player.PlayerId, ["centreCell"] = centre.ToString() });
                }

                foreach (NetworkedItem entering in desiredItems)
                {
                    if (!NetworkedLostAndFoundManager.Contains(entering.NetId))
                        player.NearbyItems[entering] = Time.time;
                }
                HostPlayerCells[player.PlayerId] = desiredCells;
                DebugRuntime.Publish("item-world", "world-item.player-cells-updated", DebugRuntimeSide.Server,
                    entityType: "Player", entityId: player.PlayerId.ToString(), data: new()
                {
                    ["centreCell"] = centre.ToString(), ["cellCount"] = desiredCells.Count,
                    ["relevantItemCount"] = desiredItems.Count
                });
            }

            foreach (NetworkedItem item in NetworkedItem.GetAll().Where(item => item != null &&
                         NetworkedLostAndFoundManager.Contains(item.NetId)).Distinct())
            {
                player.NearbyItems.Remove(item);
                if (player.Peer != NetworkLifecycle.Instance.Server.SelfPeer &&
                    !player.KnownItems.ContainsKey(item) &&
                    NetworkedLostAndFoundManager.TryCreateCollectionProjection(item, out ItemUpdateData tombstone))
                {
                    NetworkLifecycle.Instance.Server.SendItemUpdatePacket(tombstone, player);
                    player.KnownItems[item] = NetworkLifecycle.Instance.Tick;
                }
            }
        }
    }

    private static bool IsNonSpatialRelevantTo(NetworkedItem item, ServerPlayer recipient,
        HashSet<WorldItemCellCoord> recipientCells)
    {
        if (item == null || recipient == null ||
            !AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record))
            return false;
        if (record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory)
        {
            if (record.PlacementPlayerId == recipient.PlayerId) return true;
            ServerPlayer holder = NetworkLifecycle.Instance.Server.ServerPlayers.FirstOrDefault(player =>
                player.PlayerId == record.PlacementPlayerId);
            return holder != null && recipientCells.Contains(WorldItemCellCoord.FromAbsolute(holder.AbsoluteWorldPosition));
        }
        // A reserved essential-item silhouette is a non-spatial owner capability even while the
        // physical item is lying in a distant world cell. Retiring it from owner interest removes
        // the base game's Return button before Lost and Found can convert that action into a
        // retrieval. Nearby observers still receive the same item through its spatial cell index.
        if (record.PersistentOwnerPlayerId == recipient.PlayerId && HasOwnerRecallClaim(record))
            return true;
        if (record.Placement == ItemPlacementKind.TrainInterior)
        {
            // A settled train item stores an absolute position from the instant it settled, while
            // its physical representation continues moving with the car. Using that stored position
            // for interest eventually retires the item from clients who are still riding the train.
            if (record.WorldParentKind == ItemWorldParentKind.TrainInterior &&
                record.WorldParentNetId != 0)
            {
                // Keep the containing car's occupants subscribed even during origin shifts or while
                // the authoritative car transform is briefly unavailable.
                if (recipient.CarId == record.WorldParentNetId)
                    return true;
                if (NetworkedTrainCar.TryGet(record.WorldParentNetId, out TrainCar parentCar) &&
                    parentCar != null)
                {
                    Transform parent = parentCar.interior ?? parentCar.transform;
                    Vector3 currentAbsolutePosition =
                        parent.TransformPoint(record.ParentLocalPosition) - WorldMover.currentMove;
                    return recipientCells.Contains(WorldItemCellCoord.FromAbsolute(currentAbsolutePosition));
                }
            }

            // Preserve relevance during incomplete legacy/restored metadata until the train parent
            // can be resolved. New train-interior settlements take the live-parent path above.
            return recipientCells.Contains(WorldItemCellCoord.FromAbsolute(record.Position));
        }
        if (record.Placement is ItemPlacementKind.Attached or ItemPlacementKind.SnappedAttachment)
            return recipientCells.Contains(WorldItemCellCoord.FromAbsolute(record.Position));
        return false;
    }

    private static bool HasOwnerRecallClaim(AuthoritativeItemRegistry.Record record) =>
        record != null && record.InventoryClaimPlayerId == record.PersistentOwnerPlayerId &&
        record.InventoryClaimSlot >= 0 &&
        (record.InventoryClaimFlags & (ItemInventoryClaimFlags.Reserved |
            ItemInventoryClaimFlags.Locked)) != 0;

    private void ProcessChanged(uint tick)
    {
        List<ItemUpdateData> dirtyItems = [];
        float timeStamp = Time.time;
        NetworkLifecycle.Instance.Server.TryGetServerPlayer(NetworkLifecycle.Instance.Server.SelfId, out ServerPlayer hostPlayer);

        foreach (var item in NetworkedItem.GetAll())
        {
            if (item == null)
                continue;
            if (NetworkedLostAndFoundManager.Contains(item.NetId))
                continue;
            if (WorldItemPersistenceManager.IsRestorePending(item))
                continue;
            if (AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record existingRecord) &&
                existingRecord.Placement == ItemPlacementKind.Destroyed)
                continue;
            ItemUpdateData snapshot = item.GetSnapshot();
            if (snapshot != null && hostPlayer != null)
            {
                if (!AuthoritativeItemRegistry.TryApplyTransition(item, snapshot, hostPlayer,
                        ItemTransitionReason.HostLocalState, true, out string rejectionReason))
                {
                    DebugTrace.Validation("item", "Item", item.NetId.ToString(), false,
                        rejectionReason, DebugRuntimeSide.Server);
                    continue;
                }
                item.ApplyHostLocalCanonicalTransition(snapshot, hostPlayer);
                RefreshHostWorldItem(item);
                dirtyItems.Add(snapshot);
            }
        }

        //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) DirtyItems: {dirtyItems.Count}");

        foreach (var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;

            if (player.Peer == NetworkLifecycle.Instance.Server.SelfPeer)
                continue;

            List<ItemUpdateData> playerUpdates = [];

            // Process nearby items
            foreach (var nearbyItem in player.NearbyItems.Keys)
            {
                if (NetworkedLostAndFoundManager.Contains(nearbyItem.NetId))
                    continue;
                bool known = player.KnownItems.TryGetValue(nearbyItem, out uint knownTick);
                var dirtyUpdate = dirtyItems.FirstOrDefault(di => di.ItemNetId == nearbyItem.NetId);
                bool specialJobItem = !known && DoNotCreateItem(nearbyItem);
                ItemDeliveryDecision delivery = KnownItemDeliveryEvaluator.Evaluate(
                    relevant: true,
                    known,
                    knownTick,
                    nearbyItem.LastDirtyTick,
                    dirtyUpdate != null,
                    suppressCreate: false,
                    destroyed: false);

                if (delivery.Kind == ItemDeliveryKind.Create)
                {
                    // This is a new item for the player
                    //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) New item for: {player.Username}, itemNetID{nearbyItem.NetId}");

                    ItemUpdateData snapshot = nearbyItem.CreateUpdateData(ItemUpdateData.ItemUpdateType.Create);
                    if (snapshot != null && hostPlayer != null)
                    {
                        AuthoritativeItemRegistry.Record record =
                            AuthoritativeItemRegistry.Ensure(nearbyItem, hostPlayer, snapshot);
                        if (record != null)
                            AuthoritativeItemRegistry.WriteToSnapshot(record, snapshot, record.LastReason);
                        Spatial?.ApplyLatestAcceptedToSnapshot(snapshot);
                    }

                    // A runtime-rendered report cannot be reconstructed from its prefab name.
                    // Send its immutable render recipe first on the same reliable ordered channel.
                    if (nearbyItem.GetTrackedItem<JobReport>() != null &&
                        JobReportArtifactRegistry.TryGet(nearbyItem.NetId, out JobReportArtifactData reportArtifact))
                    {
                        NetworkLifecycle.Instance.Server.SendJobReportArtifact(reportArtifact, player);
                    }
                    if (delivery.MarkKnown)
                        player.KnownItems[nearbyItem] = tick;

                    // Job documents are created by the job lifecycle, but they still need the
                    // authoritative Create envelope to seed revision/ownership metadata. The
                    // client binds this snapshot to its job-created object instead of creating a
                    // second representation.
                    if (snapshot != null)
                        playerUpdates.Add(snapshot);
                    if (specialJobItem)
                        TraceItem("item.special-create-metadata-sent", nearbyItem, new()
                        {
                            ["trackedItemType"] = nearbyItem.TrackedItemType?.FullName ?? string.Empty,
                            ["playerId"] = player.PlayerId
                        });
                }
                else
                {
                    //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Item exists for: {player.Username}, {dirtyUpdate != null}");
                    if (delivery.Kind == ItemDeliveryKind.FullSync)
                        dirtyUpdate = nearbyItem.CreateUpdateData(ItemUpdateData.ItemUpdateType.FullSync);

                    if (delivery.Kind is ItemDeliveryKind.Dirty or ItemDeliveryKind.FullSync && dirtyUpdate != null)
                    {
                        Multiplayer.LogDebug(() => $"ProcessChanged({tick}) Update Type: {dirtyUpdate.UpdateType}, Item State: {dirtyUpdate.ItemState}");
                        playerUpdates.Add(dirtyUpdate);
                        player.KnownItems[nearbyItem] = tick;
                    }
                }
            }

            //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Adding {DestroyedItems.Count()} DestroyedItems for: {player.Username}");

            playerUpdates.AddRange(DestroyedItems.Where(snapshot =>
                DestroyRecipients.TryGetValue(snapshot, out HashSet<byte> recipients) &&
                recipients.Contains(player.PlayerId)));

            if (playerUpdates.Count > 0)
            {
                //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Sending {playerUpdates.Count()} to player: {player.Username}");
                NetworkLifecycle.Instance.Server.SendItemsBulkUpdatePacket(playerUpdates, player);
                Spatial?.SendActiveLeasesTo(player, playerUpdates);
            }
        }

        DestroyedItems.Clear();
        DestroyRecipients.Clear();
    }

}
