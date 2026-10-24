using System.Collections.Generic;
using System.Linq;
using DV.Utils;
using Multiplayer.Core.Items.Projection;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;
using JetBrains.Annotations;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Server.Jobs;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Data.Jobs;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using System;
using DV;
using DV.InventorySystem;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Replication;
using DV.Booklets;

namespace Multiplayer.Server.Items.Projection;

internal sealed partial class ServerItemManager
{
    // Host recipient interest, known-item lifetimes, and outbound replication.

    // Rebuilding a player's relevant set walks every item in 25 cells; near a yard that cost ~4 ms
    // a tick on the host. Rebuild at once when the item index changes (it clears HostPlayerCells)
    // or the player changes cell, otherwise a few times a second for movement-driven relevance
    // (holders, train interiors).
    private const uint InterestRefreshTicks = NetworkLifecycle.TICK_RATE / 4;
    private readonly Dictionary<byte, uint> lastInterestRefreshTick = new();

    private void UpdatePlayerItemLists()
    {
        uint tick = NetworkLifecycle.Instance.Tick;
        // Lost and Found items are few; find them once, not by scanning every item per player.
        NetworkedItem[] lostItems = LostAndFoundServer.Count == 0 ? Array.Empty<NetworkedItem>() :
            NetworkedItem.GetAll().Where(item => item != null && LostAndFoundServer.Contains(item.NetId))
                .Distinct().ToArray();
        foreach (var player in NetworkServer.Instance.ServerPlayers)
        {
            if (player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;

            WorldItemCellCoord centre = WorldItemCellCoord.FromAbsolute(player.AbsoluteWorldPosition);
            HashSet<WorldItemCellCoord> desiredCells = WorldItemCellCoord.Neighbourhood(centre);
            bool cellsChanged = !HostPlayerCells.TryGetValue(player.PlayerId, out HashSet<WorldItemCellCoord> previousCells) ||
                                !previousCells.SetEquals(desiredCells);
            bool refreshDue = cellsChanged ||
                              !lastInterestRefreshTick.TryGetValue(player.PlayerId, out uint lastRefresh) ||
                              unchecked(tick - lastRefresh) >= InterestRefreshTicks;
            if (refreshDue)
            {
                lastInterestRefreshTick[player.PlayerId] = tick;
                RefreshPlayerInterest(player, centre, desiredCells, cellsChanged);
            }

            foreach (NetworkedItem item in lostItems)
            {
                player.NearbyItems.Remove(item);
                if (player.Peer != NetworkServer.Instance.SelfPeer &&
                    !player.KnownItems.ContainsKey(item) &&
                    LostAndFoundServer.TryCreateCollectionProjection(item, out ItemUpdate tombstone))
                {
                    // An essential in its owner's Lost and Found keeps the owner's red silhouette,
                    // their recall. A tombstone only projects a copy the game already has, and an
                    // owner who joined after the collection has none: send them the item first.
                    if (TryCreateLostEssentialForOwner(item, player, out ItemUpdate create))
                        NetworkServer.Instance.SendItemUpdate(create, player);
                    NetworkServer.Instance.SendItemUpdate(tombstone, player);
                    player.KnownItems[item] = NetworkLifecycle.Instance.Tick;
                }
            }
        }
    }

    private void RefreshPlayerInterest(ServerPlayer player, WorldItemCellCoord centre,
        HashSet<WorldItemCellCoord> desiredCells, bool cellsChanged)
    {
        HashSet<NetworkedItem> RelevantItems(HashSet<WorldItemCellCoord> cells)
        {
            HashSet<NetworkedItem> items = new(HostNonSpatialItems.Where(item =>
                IsNonSpatialRelevantTo(item, player, cells) &&
                (!item.IsSceneAuthored || player.WorldItemCatalogueAccepted)));
            foreach (WorldItemCellCoord cell in cells)
            {
                if (HostItemsByCell.TryGetValue(cell, out HashSet<NetworkedItem> indexed))
                    items.UnionWith(indexed.Where(item => item != null &&
                        (!item.IsSceneAuthored || player.WorldItemCatalogueAccepted)));
            }
            return items;
        }
        HashSet<NetworkedItem> desiredItems = RelevantItems(desiredCells);
        // Hysteresis: items enter at the 3x3 neighbourhood but only leave once outside 5x5.
        // Without it, walking along a cell boundary destroyed and re-created everything on
        // the other side each time the player stepped across it.
        if (player.NearbyItems.Count > 0)
        {
            HashSet<NetworkedItem> retainable = RelevantItems(WorldItemCellCoord.Neighbourhood(centre, 2));
            desiredItems.UnionWith(player.NearbyItems.Keys.Where(item => item != null && retainable.Contains(item)));
        }
        bool membershipChanged = cellsChanged || player.NearbyItems.Count != desiredItems.Count ||
                                 player.NearbyItems.Keys.Any(item => !desiredItems.Contains(item));
        if (membershipChanged)
        {

            foreach (NetworkedItem leaving in player.NearbyItems.Keys
                         .Where(item => item == null || !desiredItems.Contains(item)).ToArray())
            {
                player.NearbyItems.Remove(leaving);
                if (leaving != null && player.Peer != NetworkServer.Instance.SelfPeer &&
                    player.KnownItems.Remove(leaving))
                {
                    // Authority ends when host interest ends. The later client acknowledgement
                    // is observability, not a lease that can extend mutation permission.
                    player.AcknowledgedWorldItems.Remove(leaving.NetId);
                    ItemUpdate retirement = leaving.CreateUpdateData(ItemUpdate.ItemUpdateType.Destroy);
                    if (retirement != null)
                    {
                        retirement.TransitionReason = ItemTransitionReason.InterestRetirement;
                        NetworkServer.Instance.SendItemUpdate(retirement, player);
                    }
                }
                if (leaving != null)
                    NetworkedItemManager.TraceItem("item.cell-interest-leave", leaving,
                        new() { ["playerId"] = player.PlayerId, ["centreCell"] = centre.ToString() });
            }

            foreach (NetworkedItem entering in desiredItems)
            {
                if (!LostAndFoundServer.Contains(entering.NetId))
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
    }

    private static bool IsNonSpatialRelevantTo(NetworkedItem item, ServerPlayer recipient,
        HashSet<WorldItemCellCoord> recipientCells)
    {
        if (item == null || recipient == null ||
            !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record))
            return false;
        // An installed gadget rides its car, and every player has every car: its item is every
        // player's to know (gadgets.md, "The installed gadget and its item").
        if (record.Placement == ItemPlacementKind.Installed)
            return true;
        bool ownerHasRecallClaim = record.Owner == recipient.Guid &&
                                   HasOwnerRecallClaim(record);
        if (record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory)
        {
            ServerPlayer holder = PlayerNumbers.Session(record.Holder);
            bool holderNearby = holder != null && recipientCells.Contains(
                WorldItemCellCoord.FromAbsolute(holder.AbsoluteWorldPosition));
            return ItemPossessionInterestPolicy.IsRelevant(ownerHasRecallClaim,
                record.Holder, recipient.Guid, holderNearby);
        }
        // A reserved essential-item silhouette is a non-spatial owner capability even while the
        // physical item is lying in a distant world cell. Retiring it from owner interest removes
        // the base game's Return button before Lost and Found can convert that action into a
        // retrieval. Nearby observers still receive the same item through its spatial cell index.
        if (ownerHasRecallClaim)
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
        // Ammo in a magazine goes where its tool goes (containers.md, "Ammo in a magazine"): vanilla
        // leaves it inactive where it was loaded, so its own position means nothing.
        if (record.Placement == ItemPlacementKind.Magazine)
            return NetworkedItem.TryGet(record.MagazineNetId, out NetworkedItem tool) && tool != null && tool != item &&
                   (recipient.KnownItems.ContainsKey(tool) || IsNonSpatialRelevantTo(tool, recipient, recipientCells) ||
                    recipientCells.Contains(WorldItemCellCoord.FromAbsolute(tool.transform.position - WorldMover.currentMove)));
        if (record.Placement is ItemPlacementKind.Attached or ItemPlacementKind.SnappedAttachment)
        {
            // Attached items (e.g. end-of-train lanterns) ride their car; the record's position is
            // where they were attached, so follow the car or they vanish once the train leaves.
            if (record.AttachedCarNetId != 0 &&
                NetworkedTrainCar.TryGet(record.AttachedCarNetId, out TrainCar attachedCar) && attachedCar != null)
                return recipient.CarId == record.AttachedCarNetId ||
                       recipientCells.Contains(WorldItemCellCoord.FromAbsolute(
                           attachedCar.transform.position - WorldMover.currentMove));
            return recipientCells.Contains(WorldItemCellCoord.FromAbsolute(record.Position));
        }
        return false;
    }

    // The Create for an essential in Lost and Found, for its owner only (item-lifecycle.md,
    // "Retrieval claim slots"): the tombstone sent after it makes it inert and leaves its silhouette.
    private static bool TryCreateLostEssentialForOwner(NetworkedItem item, ServerPlayer player,
        out ItemUpdate create)
    {
        create = null;
        if (!LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record) ||
            record.Owner != player.Guid || !HasOwnerRecallClaim(record) ||
            item.Item?.InventorySpecs?.IsEssential != true)
            return false;
        create = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Create);
        if (create == null)
            return false;
        LiveItems.WriteToSnapshot(record, create, record.LastReason);
        return true;
    }

    private static bool HasOwnerRecallClaim(LiveItems.LiveItem record) =>
        record != null && record.Owner != Guid.Empty && record.InventoryClaimSlot >= 0 &&
        (record.InventoryClaimFlags & (ItemInventoryClaimFlags.Reserved |
            ItemInventoryClaimFlags.Locked)) != 0;

    /// <summary>
    /// What another player holds or carries changes only by their own packets; the server's copy
    /// of the item just mirrors them. Applied as the server's own change, its observation of that
    /// (taking it in a hand, a lantern lit in one) booked the item to the server's player: the
    /// next update moved it out of the player's hand to the server's body, and it vanished for
    /// them. Such a snapshot keeps only the server's own object state (and is skipped if it has
    /// none): the rest is the holder's, and the server's copy drifting (a flashlight draining in
    /// their hand with no sun on its panel) overwrote what their game had.
    /// </summary>
    private static bool TryKeepRemotePossession(NetworkedItem item, ItemUpdate snapshot, ServerPlayer hostPlayer)
    {
        if (snapshot.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create) ||
            snapshot.ItemState is not (ItemState.InHand or ItemState.InInventory) ||
            snapshot.PlayerId == 0 || snapshot.PlayerId == hostPlayer?.PlayerId)
            return true;
        snapshot.UpdateType &= ItemUpdate.ItemUpdateType.ObjectState;
        if (snapshot.UpdateType == ItemUpdate.ItemUpdateType.None)
            return false;
        item.KeepHostReportsForRemoteHolder(snapshot);
        return snapshot.States is { Count: > 0 };
    }

    private void ProcessChanged(uint tick)
    {
        List<ItemUpdate> dirtyItems = [];
        float timeStamp = Time.time;
        // Null with nobody to commit the host's changes yet, or on a host with no player of its own,
        // where the server commits them itself (HostItemActor).
        ServerPlayer hostPlayer = HostItemActor.Player;
        bool hostActs = hostPlayer != null || HostItemActor.IsServer;

        foreach (var item in NetworkedItem.GetAll())
        {
            if (item == null)
                continue;
            if (LostAndFoundServer.Contains(item.NetId))
                continue;
            if (ItemSaveLoad.IsRestorePending(item))
                continue;
            // An installed gadget's item changes only with its gadget (Server/Gadgets).
            if (LiveItems.TryGet(item.NetId, out LiveItems.LiveItem existingRecord) &&
                existingRecord.Placement is ItemPlacementKind.Destroyed or ItemPlacementKind.Installed)
                continue;
            uint dirtyBefore = item.LastDirtyTick;
            ItemUpdate snapshot = item.GetSnapshot();
            if (snapshot != null && hostActs && !TryKeepRemotePossession(item, snapshot, hostPlayer))
            {
                // Nothing of it is the server's to send, so it isn't a change: as one, each player
                // who knew the item was sent the server's copy whole instead (a FullSync), its
                // drift and all (a lantern's wick swinging in the holder's projected hand).
                item.KeepLastDirtyTick(dirtyBefore);
                continue;
            }
            if (snapshot != null && hostActs)
            {
                string rejectionReason;
                bool accepted = hostPlayer != null
                    ? LiveItems.TryApplyTransition(item, snapshot, hostPlayer,
                        ItemTransitionReason.HostLocalState, true, out rejectionReason)
                    : LiveItems.TryApplyServerTransition(item, snapshot,
                        ItemTransitionReason.HostLocalState, true, out rejectionReason);
                if (!accepted)
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

        //NetworkServer.Instance.LogDebug(() => $"ProcessChanged({tick}) DirtyItems: {dirtyItems.Count}");

        foreach (var player in NetworkServer.Instance.ServerPlayers)
        {
            if (player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;

            if (player.Peer == NetworkServer.Instance.SelfPeer)
                continue;

            List<ItemUpdate> playerUpdates = [];

            // Process nearby items
            foreach (var nearbyItem in player.NearbyItems.Keys)
            {
                if (LostAndFoundServer.Contains(nearbyItem.NetId))
                    continue;
                bool known = player.KnownItems.TryGetValue(nearbyItem, out uint knownTick);
                var dirtyUpdate = dirtyItems.FirstOrDefault(di => di.ItemNetId == nearbyItem.NetId);
                bool specialJobItem = !known && shell.DoNotCreateItem(nearbyItem);
                ItemDeliveryDecision delivery = KnownItemDeliveryEvaluator.Evaluate(
                    relevant: true,
                    known,
                    knownTick,
                    nearbyItem.LastDirtyTick,
                    dirtyUpdate != null,
                    suppressCreate: false,
                    destroyed: false);

                if (delivery.Kind == ItemDeliveryKind.Create &&
                    player.ProjectionRetryAt.TryGetValue(nearbyItem.NetId, out float retryAt))
                {
                    if (Time.time < retryAt)
                        continue;
                    player.ProjectionRetryAt.Remove(nearbyItem.NetId);
                }

                if (delivery.Kind == ItemDeliveryKind.Create)
                {
                    // This is a new item for the player
                    //NetworkServer.Instance.LogDebug(() => $"ProcessChanged({tick}) New item for: {player.Username}, itemNetID{nearbyItem.NetId}");

                    ItemUpdate snapshot = nearbyItem.CreateUpdateData(ItemUpdate.ItemUpdateType.Create);
                    if (snapshot != null && hostActs)
                    {
                        LiveItems.LiveItem record =
                            LiveItems.Ensure(nearbyItem, hostPlayer, snapshot);
                        if (record != null)
                            LiveItems.WriteToSnapshot(record, snapshot, record.LastReason);
                        shell.Spatial?.ApplyLatestAcceptedToSnapshot(snapshot);
                    }

                    // A runtime-rendered report cannot be reconstructed from its prefab name.
                    // Send its immutable render recipe first on the same reliable ordered channel.
                    if (nearbyItem.GetTrackedItem<JobReport>() != null &&
                        JobReportArtifactRegistry.TryGet(nearbyItem.NetId, out JobReportArtifactData reportArtifact))
                    {
                        NetworkServer.Instance.SendJobReportArtifact(reportArtifact, player);
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
                        NetworkedItemManager.TraceItem("item.special-create-metadata-sent", nearbyItem, new()
                        {
                            ["trackedItemType"] = nearbyItem.TrackedItemType?.FullName ?? string.Empty,
                            ["playerId"] = player.PlayerId
                        });
                }
                else
                {
                    //NetworkServer.Instance.LogDebug(() => $"ProcessChanged({tick}) Item exists for: {player.Username}, {dirtyUpdate != null}");
                    if (delivery.Kind == ItemDeliveryKind.FullSync)
                        dirtyUpdate = nearbyItem.CreateUpdateData(ItemUpdate.ItemUpdateType.FullSync);

                    if (delivery.Kind is ItemDeliveryKind.Dirty or ItemDeliveryKind.FullSync && dirtyUpdate != null)
                    {
                        Multiplayer.LogDebug(() => $"ProcessChanged({tick}) Update Type: {dirtyUpdate.UpdateType}, Item State: {dirtyUpdate.ItemState}");
                        playerUpdates.Add(dirtyUpdate);
                        player.KnownItems[nearbyItem] = tick;
                    }
                }
            }

            //NetworkServer.Instance.LogDebug(() => $"ProcessChanged({tick}) Adding {DestroyedItems.Count()} DestroyedItems for: {player.Username}");

            playerUpdates.AddRange(DestroyedItems.Where(snapshot =>
                DestroyRecipients.TryGetValue(snapshot, out HashSet<byte> recipients) &&
                recipients.Contains(player.PlayerId)));

            if (playerUpdates.Count > 0)
            {
                //NetworkServer.Instance.LogDebug(() => $"ProcessChanged({tick}) Sending {playerUpdates.Count()} to player: {player.Username}");
                NetworkServer.Instance.SendItemUpdates(playerUpdates, player);
                HostSpatial.SendActiveLeasesTo(player, playerUpdates);
            }
        }

        DestroyedItems.Clear();
        DestroyRecipients.Clear();
    }

}
