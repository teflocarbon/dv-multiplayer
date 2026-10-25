using System.Collections.Generic;
using System.Linq;
using DV.Utils;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
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
    // a tick on the host. Rebuild at once when an item moves into or within the index (that clears
    // HostPlayerCells; one taken out of it waits for the next rebuild) or the player changes cell,
    // otherwise a few times a second for movement-driven relevance (holders, train interiors).
    private const uint InterestRefreshTicks = NetworkLifecycle.TICK_RATE / 4;
    private readonly Dictionary<byte, uint> lastInterestRefreshTick = new();
    // Items a player's interest keeps whatever their cells say, each until a time: a pending train
    // wake's item, sent to the players who could simulate it (ServerItemSpatial, OI-PHY-12).
    private readonly Dictionary<byte, Dictionary<NetworkedItem, float>> pinnedInterest = new();

    /// <summary>Keeps <paramref name="item"/> in <paramref name="player"/>'s interest for <paramref name="seconds"/>.</summary>
    internal void PinInterest(ServerPlayer player, NetworkedItem item, float seconds)
    {
        if (player == null || item == null)
            return;
        if (!pinnedInterest.TryGetValue(player.PlayerId, out Dictionary<NetworkedItem, float> pins))
            pinnedInterest[player.PlayerId] = pins = new();
        pins[item] = Time.realtimeSinceStartup + seconds;
        // Their next refresh is now, not in a quarter of a second.
        lastInterestRefreshTick.Remove(player.PlayerId);
    }

    /// <summary>How long <paramref name="item"/> stays pinned in this player's interest, s (0 when it isn't).</summary>
    internal float PinnedFor(ServerPlayer player, NetworkedItem item) =>
        player != null && item != null &&
        pinnedInterest.TryGetValue(player.PlayerId, out Dictionary<NetworkedItem, float> pins) &&
        pins.TryGetValue(item, out float until)
            ? Mathf.Max(0f, until - Time.realtimeSinceStartup)
            : 0f;

    private void AddPinnedInterest(ServerPlayer player, HashSet<NetworkedItem> desiredItems)
    {
        // What they simulate stays theirs until its lease ends, wherever it flies.
        if (player.Peer != NetworkServer.Instance.SelfPeer)
            desiredItems.UnionWith(HostSpatial.SimulatedBy(player.PlayerId)
                .Where(item => !LostAndFoundServer.Contains(item.NetId)));
        if (!pinnedInterest.TryGetValue(player.PlayerId, out Dictionary<NetworkedItem, float> pins))
            return;
        float now = Time.realtimeSinceStartup;
        foreach (KeyValuePair<NetworkedItem, float> pin in pins.ToArray())
        {
            if (pin.Key == null || pin.Value < now)
                pins.Remove(pin.Key);
            else if (player.TakesAuthored(pin.Key))
                desiredItems.Add(pin.Key);
        }
        if (pins.Count == 0)
            pinnedInterest.Remove(player.PlayerId);
    }

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
                // An item that left the player's interest starts afresh when it comes back.
                if (player.ProjectionRetries.Count > 0)
                    foreach (ushort netId in player.ProjectionRetries.Keys.ToArray())
                        if (!NetworkedItem.TryGet(netId, out NetworkedItem retried) || retried == null ||
                            !player.NearbyItems.ContainsKey(retried))
                            player.ProjectionRetries.Remove(netId);
            }

            foreach (NetworkedItem item in lostItems)
            {
                player.NearbyItems.Remove(item);
                if (player.Peer != NetworkServer.Instance.SelfPeer &&
                    !player.KnownItems.ContainsKey(item) &&
                    LostAndFoundServer.TryCreateCollectionProjection(item, out ItemUpdate tombstone))
                {
                    // A tombstone only projects a copy the game already has. An owner with none (who
                    // joined after the collection) has their essential's silhouette from its Lost
                    // and Found row instead (LostAndFoundSilhouettes).
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
                player.TakesAuthored(item)));
            foreach (WorldItemCellCoord cell in cells)
            {
                if (HostItemsByCell.TryGetValue(cell, out HashSet<NetworkedItem> indexed))
                    items.UnionWith(indexed.Where(item => item != null &&
                        player.TakesAuthored(item)));
            }
            return items;
        }
        // Items enter at the 3x3 neighbourhood but only leave once outside 5x5 (InterestCells).
        HashSet<NetworkedItem> desiredItems = InterestCells.Wanted(
            radius => RelevantItems(radius == InterestCells.EnterRadius ? desiredCells : WorldItemCellCoord.Neighbourhood(centre, radius)),
            player.NearbyItems.Count > 0,
            item => item != null && player.NearbyItems.ContainsKey(item));
        AddPinnedInterest(player, desiredItems);
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
                        // Why it left this player's game, on the server and with the Destroy (OI-SYNC-18).
                        retirement.RemovalCause = new ChangeCause<ItemRemovalReason>(ItemRemovalReason.LeftInterest,
                            tick: NetworkLifecycle.Instance.Tick, detail: WhyNotRelevant(leaving, centre));
                        DebugRuntime.Publish("item-world", "world-item.interest-retired", DebugRuntimeSide.Server,
                            entityType: "Item", entityId: leaving.NetId.ToString(), data: new()
                            {
                                ["playerId"] = player.PlayerId,
                                ["centreCell"] = centre.ToString(),
                                ["cause"] = retirement.RemovalCause.Value.ToString()
                            });
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

    // Where the item is that no longer concerns the player, for the retirement's cause.
    private string WhyNotRelevant(NetworkedItem item, WorldItemCellCoord centre)
    {
        if (!LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record))
            return "no record";
        if (LostAndFoundServer.Contains(item.NetId))
            return "in Lost and Found";
        return HostCellByItem.TryGetValue(item, out WorldItemCellCoord cell)
            ? $"{record.Placement} in cell {cell}, the player's {centre}"
            : $"{record.Placement}, not near the player";
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
            TrainCar attachedCar = CarOf(item, record);
            if (attachedCar != null && NetworkedTrainCar.TryGetNetId(attachedCar, out ushort carNetId))
                return recipient.CarId == carNetId ||
                       recipientCells.Contains(WorldItemCellCoord.FromAbsolute(
                           attachedCar.transform.position - WorldMover.currentMove));
            return recipientCells.Contains(WorldItemCellCoord.FromAbsolute(record.Position));
        }
        return false;
    }

    // The car an attached or snapped item rides: a coupler's names it in the record; a gadget's snap
    // point names only the gadget, so its car is the installed gadget's, else the snap point's own.
    private static TrainCar CarOf(NetworkedItem item, LiveItems.LiveItem record)
    {
        if (record.AttachedCarNetId != 0 && NetworkedTrainCar.TryGet(record.AttachedCarNetId, out TrainCar car) && car != null)
            return car;
        if (record.Placement != ItemPlacementKind.SnappedAttachment)
            return null;
        if (LiveItems.TryGet(record.WorldParentNetId, out LiveItems.LiveItem gadget) && gadget.AttachedCarNetId != 0 &&
            NetworkedTrainCar.TryGet(gadget.AttachedCarNetId, out car) && car != null)
            return car;
        DV.Items.Snapping.ItemSnapPointBase point = item.Item?.SnappableItem?.SnappedTo;
        return point != null ? TrainCar.Resolve(point.gameObject) : null;
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
    private static bool TryKeepRemotePossession(NetworkedItem item, ItemUpdate snapshot, ServerPlayer hostPlayer,
        LiveItems.LiveItem record)
    {
        if (snapshot.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create))
            return true;
        // The record says so too: an object made for an item kept as data in their inventory lies
        // inactive at the safety position, and its own reading (Dropped, in the world) was taken as
        // the server's move, out of their slot and into the world (stand-in-object-state).
        if ((snapshot.ItemState is not (ItemState.InHand or ItemState.InInventory) ||
             snapshot.PlayerId == 0 || snapshot.PlayerId == hostPlayer?.PlayerId) &&
            !HeldByAnotherPlayer(record, hostPlayer))
            return true;
        snapshot.UpdateType &= ItemUpdate.ItemUpdateType.ObjectState;
        if (snapshot.UpdateType == ItemUpdate.ItemUpdateType.None)
            return false;
        item.KeepHostReportsForRemoteHolder(snapshot);
        return snapshot.States is { Count: > 0 };
    }

    private static bool HeldByAnotherPlayer(LiveItems.LiveItem record, ServerPlayer hostPlayer) =>
        record != null && record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory &&
        record.Holder != Guid.Empty && record.Holder != hostPlayer?.Guid;

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
            // What another player holds is theirs: its copy here runs too, and drifts from them. So
            // does one they simulate (their drop or throw, in flight): the copy here follows their
            // samples, and a lantern's knob swung its wick from the row's 0.2 to 0.4 as it fell, sent
            // as the host's own change (retrieved-state). And for a moment after it settles, while
            // the knob may still swing. Not while our own player works it.
            if (hostActs && (HeldByAnotherPlayer(existingRecord, hostPlayer) ||
                             HostSpatial.SimulatedByAnother(item.NetId) && !item.IsLocalInteractionOpen))
                item.RevertRemoteHolderDrift();
            uint dirtyBefore = item.LastDirtyTick;
            ItemUpdate snapshot = item.GetSnapshot();
            if (snapshot != null && hostActs && !TryKeepRemotePossession(item, snapshot, hostPlayer, existingRecord))
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

                // The failures are counted until the client makes it or it leaves interest.
                if (delivery.Kind == ItemDeliveryKind.Create &&
                    player.ProjectionRetries.TryGetValue(nearbyItem.NetId, out var retry) && Time.time < retry.At)
                    continue;

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
                    // A new item's own first snapshot is a Create: a player it was already made for (by
                    // the Create above, a tick before) gets it whole instead, not made a second time.
                    // A game that couldn't make it failed it twice, and once more than its retries.
                    if (delivery.Kind == ItemDeliveryKind.FullSync ||
                        delivery.Kind == ItemDeliveryKind.Dirty &&
                        dirtyUpdate?.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create) == true)
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
