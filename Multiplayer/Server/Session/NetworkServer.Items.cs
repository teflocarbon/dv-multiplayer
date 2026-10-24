using DV.InventorySystem;
using LiteNetLib;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.OwnedList;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Jobs;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Transport;
using Multiplayer.Debugging.Protocol;
using ItemRemovalReason = Multiplayer.Core.Items.ChangeCauses.ItemRemovalReason;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Session;

// NetworkServer's items handlers and sends (see docs/design/code-layout.md).
public partial class NetworkServer : IItemServer
{
    bool IItemServer.IsInLostAndFound(ushort itemNetId) => LostAndFoundServer.Contains(itemNetId);

    bool IItemServer.TryGetPersistentOwner(ushort itemNetId, out Guid owner)
    {
        bool known = LiveItems.TryGet(itemNetId, out LiveItems.LiveItem record);
        owner = known ? record.Owner : Guid.Empty;
        return known;
    }

    bool IItemServer.TryGetPlacement(ushort itemNetId, out ItemPlacementKind placement)
    {
        bool known = LiveItems.TryGet(itemNetId, out LiveItems.LiveItem record);
        placement = known ? record.Placement : default;
        return known;
    }

    void IItemServer.MarkAuthoredIdentity(NetworkedItem item) => LiveItems.MarkAuthoredIdentity(item);

    void IItemServer.PromoteAuthoredToPersistent(NetworkedItem item) => LiveItems.PromoteAuthoredToPersistent(item);

    void IItemServer.OnItemDestroyed(NetworkedItem item, bool suppressNetworkDestroy)
    {
        item.OnDestroyed(suppressNetworkDestroy);
        JobReportArtifactRegistry.Remove(item.NetId);
    }


    IItemManagerServerPart IItemServer.CreateItemManagerPart(NetworkedItemManager manager) => new ServerItemManager(manager);

    INetworkedItemServerPart IItemServer.CreateItemPart(NetworkedItem item) => new NetworkedItemServerPart(item);

    Dictionary<string, object> IItemServer.AuthorityDebugState(ushort itemNetId) =>
        LiveItems.TryGet(itemNetId, out LiveItems.LiveItem record)
            ? LiveItems.Snapshot(record) : null;

    Dictionary<string, object>[] IItemServer.LostAndFoundDebugSnapshot() => LostAndFoundServer.DebugSnapshot();

    Dictionary<string, object>[] IItemServer.ColdContainerDebugSnapshot() => ContainerServer.DebugSnapshot();

    private void OnServerboundItemSpatialSamplePacket(ServerboundItemSpatialSamplePacket packet,
        ITransportPeer peer)
    {
        if (packet?.State == null || !TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        ServerItemManager.Instance?.ReceiveItemSpatialSample(packet.State, player);
    }

    private void OnServerboundItemSpatialSettlementPacket(ServerboundItemSpatialSettlementPacket packet,
        ITransportPeer peer)
    {
        if (packet?.State == null || !TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        ServerItemManager.Instance?.ReceiveItemSpatialSettlement(packet.State, player);
    }

    private void OnServerboundItemTrainWakeWitnessPacket(
        ServerboundItemTrainWakeWitnessPacket packet, ITransportPeer peer)
    {
        if (packet == null || !TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        ServerItemManager.Instance?.ReceiveItemTrainWakeWitness(packet, player);
    }

    private void OnServerboundWorldItemCataloguePacket(ServerboundWorldItemCataloguePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        var host = NetworkedItemManager.Instance.GetAuthoredCatalogueSummary();
        bool accepted = packet != null && packet.CollisionCount == 0 && host.Collisions == 0 &&
                        packet.ItemCount == host.Count && string.Equals(packet.Digest, host.Digest, StringComparison.Ordinal);
        player.WorldItemCatalogueAccepted = accepted;
        string reason = accepted ? string.Empty :
            $"catalogue-mismatch:host={host.Count}/{host.Digest}/c{host.Collisions},client={packet?.ItemCount ?? 0}/{packet?.Digest ?? string.Empty}/c{packet?.CollisionCount ?? 0}";
        // A rejected player gets no scene items (shop and office props) for the whole session.
        if (!accepted)
            LogWarning($"Scene item catalogue rejected for {player.Username}: {reason}");
        DebugRuntime.Publish("item-world", "world-item.catalogue-negotiated", DebugRuntimeSide.Server,
            accepted ? DebugSeverity.Info : DebugSeverity.Error, "Player", player.PlayerId.ToString(), new()
            {
                ["accepted"] = accepted, ["reason"] = reason,
                ["hostCount"] = host.Count, ["clientCount"] = packet?.ItemCount ?? 0
            });
        SendPacket(peer, new ClientboundWorldItemCataloguePacket
        {
            Accepted = accepted,
            HostItemCount = host.Count,
            HostCollisionCount = host.Collisions,
            HostDigest = host.Digest,
            Reason = reason
        }, DeliveryMethod.ReliableOrdered);
    }

    private void OnServerboundWorldItemProjectionAckPacket(ServerboundWorldItemProjectionAckPacket packet, ITransportPeer peer)
    {
        if (packet == null || !TryGetServerPlayer(peer, out ServerPlayer player)) return;
        bool known = NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem item) && item != null;
        // An ack names the revision the client applied. Another player's update can move the item
        // on while it's in flight (everyone near a new job overview sends one as it appears); the
        // later revisions follow it on the same reliable ordered channel, so an older one still
        // means the client has the item. Only an ack from beyond the server's revision is wrong.
        // Requiring an exact match left those items unacknowledged, and their pickups refused.
        bool revisionMatches = known && LiveItems.TryGet(packet.ItemNetId, out var record) &&
                               packet.AuthorityRevision <= record.Revision;
        if (packet.Projected && (!known || !revisionMatches || !player.KnownItems.ContainsKey(item)))
        {
            DebugRuntime.Publish("item-world", "world-item.projection-ack-rejected", DebugRuntimeSide.Server,
                DebugSeverity.Warning, "Item", packet.ItemNetId.ToString(), new()
                {
                    ["playerId"] = player.PlayerId,
                    ["knownItem"] = known,
                    ["revisionMatches"] = revisionMatches,
                    ["recipientExpectedItem"] = known && player.KnownItems.ContainsKey(item)
                });
            return;
        }
        if (packet.Projected) player.AcknowledgedWorldItems.Add(packet.ItemNetId);
        else player.AcknowledgedWorldItems.Remove(packet.ItemNetId);
        // A client that couldn't create an item we still consider relevant doesn't have it:
        // forget that it does, so the Create is sent again (after a pause, in case it keeps failing)
        // instead of a stream of updates for an object it never made.
        if (!packet.Projected && known && player.KnownItems.Remove(item))
            player.ProjectionRetryAt[packet.ItemNetId] = Time.time + ProjectionRetryDelaySeconds;
        DebugRuntime.Publish("item-world", packet.Projected ? "world-item.projection-enter-ack" : "world-item.projection-retire-ack",
            DebugRuntimeSide.Server, entityType: "Item", entityId: packet.ItemNetId.ToString(), data: new()
            {
                ["playerId"] = player.PlayerId,
                ["authorityRevision"] = packet.AuthorityRevision,
                ["authoredItemKey"] = known && LiveItems.TryGet(packet.ItemNetId,
                    out LiveItems.LiveItem acknowledgedRecord)
                    ? acknowledgedRecord.AuthoredItemKey ?? string.Empty
                    : string.Empty
            });
    }

    // ---- Updates (docs/design/items/wire.md, "One update") ----

    /// <summary>Item changes for one player (a join, its interest moving), built for it.</summary>
    public void SendItemUpdates(List<ItemUpdate> updates, ServerPlayer player)
    {
        // Item packets go out many times a second: they're traced (the debug system), not logged.
        DebugRuntime.Publish("item", "item.bulk-send-requested", DebugRuntimeSide.Server, data: new()
        {
            ["count"] = updates?.Count ?? 0,
            ["playerId"] = player?.PlayerId ?? 0,
            ["itemNetIds"] = string.Join(",", updates?.Select(item => item?.ItemNetId ?? 0) ?? Enumerable.Empty<ushort>())
        });
        if (player != null)
            SendItemUpdatesTo(player, updates);
        else
            foreach (ServerPlayer recipient in ServerPlayers.ToArray())
                if (recipient.LoadingState >= PlayerLoadingState.ReadyForItems)
                    SendItemUpdatesTo(recipient, updates);
    }

    /// <summary>
    /// One item change, for <paramref name="sendToPlayer"/> only or for everyone but
    /// <paramref name="excludePlayer"/>, each copy built for its recipient.
    /// </summary>
    public void SendItemUpdate(ItemUpdate item, ServerPlayer sendToPlayer = null, ServerPlayer excludePlayer = null)
    {
        DebugRuntime.Publish("item", "item.relay-requested", DebugRuntimeSide.Server, entityType: "Item", entityId: item?.ItemNetId.ToString(),
            data: Merge(DebugTrace.ItemSnapshotData(item), new() { ["sendToPlayer"] = sendToPlayer?.PlayerId ?? 0, ["excludePlayer"] = excludePlayer?.PlayerId ?? 0 }));
        if (item == null)
            return;
        List<ItemUpdate> one = new() { item };
        if (sendToPlayer != null)
            SendItemUpdatesTo(sendToPlayer, one);
        else
            foreach (ServerPlayer recipient in ServerPlayers.ToArray())
                if (recipient != excludePlayer && recipient.LoadingState >= PlayerLoadingState.ReadyForItems)
                    SendItemUpdatesTo(recipient, one);
    }

    /// <summary>
    /// Rows of a player's inventory (OI-COLD-03, D14): each Create built for them, beside its saved
    /// state. <paramref name="full"/> is the whole inventory (the join), sent even when empty.
    /// </summary>
    public void SendInventoryRows(ServerPlayer recipient, List<ItemUpdate> rows, List<string> states, bool full = false)
    {
        if (recipient?.Peer == null || recipient == LocalPlayer || rows == null || rows.Count == 0 && !full)
            return;
        ClientboundInventoryRowsPacket packet = new()
        {
            Full = full,
            Rows = rows.Select(row => BuildFor(row, recipient)).ToList(),
            States = states.ToArray()
        };
        SendPacket(recipient.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    // The host's own player is the server's own world: it's never sent its items' changes.
    private void SendItemUpdatesTo(ServerPlayer recipient, List<ItemUpdate> updates)
    {
        if (recipient?.Peer == null || recipient == LocalPlayer || updates == null)
            return;
        List<ItemUpdate> built = new(updates.Count);
        foreach (ItemUpdate update in updates)
            if (update != null)
                built.Add(BuildFor(update, recipient));
        if (built.Count == 0)
            return;
        ClientboundItemUpdatesPacket packet = new() { Updates = built };
        TraceItemDeliveryExpected(packet, recipient.Peer, DeliveryMethod.ReliableOrdered);
        SendPacket(recipient.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    // What one player is told of an item: its relation to it, never the owner (D3), and the claim
    // only if it's the claimant. A Destroy, or an item with no record, is nobody's, except a Lost and
    // Found tombstone: its owner keeps the essential's silhouette by it (OI-ESS-08).
    private static ItemUpdate BuildFor(ItemUpdate update, ServerPlayer recipient)
    {
        if (update.UpdateType == ItemUpdate.ItemUpdateType.Destroy &&
            update.TransitionReason != ItemTransitionReason.LostAndFoundCollection ||
            !LiveItems.TryGet(update.ItemNetId, out LiveItems.LiveItem record))
            return update.For(ItemRelation.Nobodys, false);
        return update.For(ItemRelations.Of(record.Owner, recipient.Guid),
            recipient.Guid != Guid.Empty && LiveItems.Claimant(record) == recipient.Guid);
    }

    // The spatial stream is for the players following an item. The host's own player is the
    // server's own world: when it simulates, the server's lease is the lease, and its session
    // presents none of these, so it isn't sent them.
    public void SendItemSpatialLease(NetworkedItem item, ClientboundItemSpatialLeasePacket packet)
    {
        if (item == null || packet?.State == null)
            return;
        ServerPlayer local = LocalPlayer;
        foreach (ServerPlayer player in ServerPlayers)
        {
            if (player?.Peer == null || player == local || player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;
            bool simulator = player.PlayerId == packet.State.SimulatorPlayerId;
            if (!simulator && !player.KnownItems.ContainsKey(item))
                continue;
            SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
        }
    }

    public void SendItemSpatialLease(ServerPlayer player, ClientboundItemSpatialLeasePacket packet)
    {
        if (player?.Peer == null || packet?.State == null || player == LocalPlayer ||
            player.LoadingState < PlayerLoadingState.ReadyForItems)
            return;
        SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendItemSpatialSample(NetworkedItem item, ClientboundItemSpatialSamplePacket packet,
        byte simulatorPlayerId)
    {
        if (item == null || packet?.State == null)
            return;
        ServerPlayer local = LocalPlayer;
        foreach (ServerPlayer player in ServerPlayers)
        {
            if (player?.Peer == null || player == local || player.PlayerId == simulatorPlayerId ||
                player.LoadingState < PlayerLoadingState.ReadyForItems || !player.KnownItems.ContainsKey(item))
                continue;
            // Ordering is per item via simulationEpoch/sampleSequence. LiteNetLib's Sequenced
            // channel is connection-wide and would let a busy item suppress another item's
            // samples, so use ordinary unreliable delivery here.
            SendPacket(player.Peer, packet, DeliveryMethod.Unreliable);
        }
    }

    public void SendItemSpatialCommit(NetworkedItem item, ClientboundItemSpatialCommitPacket packet)
    {
        if (item == null || packet?.State == null)
            return;
        ServerPlayer local = LocalPlayer;
        foreach (ServerPlayer player in ServerPlayers)
        {
            if (player?.Peer == null || player == local || player.LoadingState < PlayerLoadingState.ReadyForItems ||
                player.PlayerId != packet.State.SimulatorPlayerId && !player.KnownItems.ContainsKey(item))
                continue;
            SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
        }
    }

    private void TraceItemDeliveryExpected<T>(T packet, ITransportPeer peer, DeliveryMethod deliveryMethod)
    {
        if (packet is not ClientboundItemUpdatesPacket { Updates: not null } updates ||
            !DebugRuntime.EnabledFor("item") || peer == null ||
            !TryGetServerPlayer(peer, out ServerPlayer recipient)) return;
        foreach (ItemUpdate snapshot in updates.Updates.Where(item => item != null))
        {
            Dictionary<string, object> data = DebugTrace.ItemSnapshotData(snapshot);
            data["packetType"] = nameof(ClientboundItemUpdatesPacket);
            data["recipientPlayerId"] = recipient.PlayerId;
            data["recipientPlayerName"] = recipient.Username ?? string.Empty;
            data["recipientPeerId"] = peer.Id;
            data["delivery"] = deliveryMethod.ToString();
            data["loadingState"] = recipient.LoadingState.ToString();
            if (NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem networkedItem) && networkedItem != null)
            {
                bool nearby = recipient.NearbyItems.TryGetValue(networkedItem, out float nearbyTime);
                bool known = recipient.KnownItems.TryGetValue(networkedItem, out uint knownTick);
                data["nearby"] = nearby; data["nearbyAgeSeconds"] = nearby ? Time.time - nearbyTime : 0f;
                data["known"] = known; data["knownTick"] = knownTick; data["lastDirtyTick"] = networkedItem.LastDirtyTick;
                data["decision"] = !nearby ? "outside-interest-but-sent" : !known ? "create-required" : knownTick < networkedItem.LastDirtyTick ? "full-sync-required" : "relay/update";
            }
            DebugRuntime.Publish("item", "item.delivery-expected", DebugRuntimeSide.Server, entityType: "Item", entityId: snapshot.ItemNetId.ToString(), data: data);
        }
    }

    // ---- Requests (docs/design/items/wire.md, "One request, one result") ----

    // The host's own player's requests are handled in-process; the answers go to its peer like
    // anyone's, and its session shows them (LocalPlayerSession.Items).
    partial void RegisterLocalItems()
    {
        HandleLocal<ServerboundItemRequestPacket>(HandleItemRequest);
        HandleLocal<ServerboundItemListRequestPacket>(HandleItemListRequest);
        HandleLocal<ServerboundStashedCassettePacket>(HandleStashedCassette);
    }

    private void OnServerboundItemRequestPacket(ServerboundItemRequestPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HandleItemRequest(packet, player);
        else
            DebugTrace.Validation("item", "Item", packet?.Request?.ItemNetId.ToString(), false, "unknown-sender", DebugRuntimeSide.Server);
    }

    /// <summary>
    /// One request: carried out once, answered once. A repeat of its id gets the remembered
    /// answer and does nothing else; a repeat while it's still being carried out (a withdrawal
    /// materializing) gets nothing, as its answer is on its way.
    /// </summary>
    private void HandleItemRequest(ServerboundItemRequestPacket packet, ServerPlayer player)
    {
        ItemRequest request = packet?.Request;
        if (request == null || player == null)
            return;
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Server, "Item",
            request.ItemNetId != 0 ? request.ItemNetId.ToString() : "h" + request.ItemHandle);
        ItemRequestStart start = player.ItemRequests.Begin(request.RequestId, out ClientboundItemResultPacket remembered);
        if (start != ItemRequestStart.New)
        {
            DebugRuntime.Publish("item", "item.request-repeated", DebugRuntimeSide.Server,
                DebugSeverity.Warning, "Item", request.ItemNetId.ToString(), new()
                {
                    ["playerId"] = player.PlayerId,
                    ["requestId"] = request.RequestId,
                    ["action"] = request.Action.ToString(),
                    ["start"] = start.ToString()
                });
            if (start == ItemRequestStart.Repeat)
                SendItemResult(player, remembered);
            else if (start == ItemRequestStart.Expired)
                SendItemResult(player, Refuse(request, "request-expired", ItemRefusal.Expired));
            return;
        }
        ClientboundItemResultPacket result = Carry(request, player);
        if (result != null)
            CompleteItemRequest(player, result);
    }

    private ClientboundItemResultPacket Carry(ItemRequest request, ServerPlayer player)
    {
        // No request is carried out before the load has run (save-load.md, "The load"; OI-PER-23):
        // one made before it would act on items the load hasn't placed, and the load would wipe it.
        if (!ServerLedger.Loaded)
            return Refuse(request, "items-loading", ItemRefusal.Busy);
        // An item kept as data on the server (inventory.md, "Data only on the server"; OI-COLD-06)
        // has its object made again first; the request is answered once it has.
        ushort needed = DataOnlyNamed(request, player);
        if (needed != 0)
        {
            PlayerItems.WithObject(needed, made =>
            {
                if (player.Peer == null)
                    return;
                ClientboundItemResultPacket later = made != null ? Carry(request, player)
                    : Refuse(request, "item-data-unavailable", ItemRefusal.UnknownItem);
                if (later != null)
                    CompleteItemRequest(player, later);
            });
            return null;
        }
        switch (request.Action)
        {
            case ItemAction.Take:
            case ItemAction.Stash:
            case ItemAction.Drop:
            case ItemAction.Throw:
            case ItemAction.Attach:
            case ItemAction.SetState:
            case ItemAction.LoadMagazine:
                return CarryChange(request, player);
            case ItemAction.Recall:
                return CarryRecall(request, player);
            case ItemAction.Retrieve:
                return CarryRetrieve(request, player);
            case ItemAction.CollectLost:
                return CarryCollectLost(request, player);
            case ItemAction.Deposit:
                return CarryDeposit(request, player);
            case ItemAction.Withdraw:
                return CarryWithdraw(request, player);
            case ItemAction.MoveInContainer:
                return CarryMoveInContainer(request, player);
            case ItemAction.DepositMoney:
                return CarryDepositMoney(request, player);
            case ItemAction.ReportBelowMap:
                return CarryBelowMapReport(request, player);
            case ItemAction.MoveSlot:
                return CarryMoveSlot(request, player);
            default:
                return Refuse(request, "item-action-invalid", ItemRefusal.Invalid);
        }
    }

    // The item a request names, the container shell it opens, or the one it would swap with or
    // take back, when it's data only.
    private static ushort DataOnlyNamed(ItemRequest request, ServerPlayer player)
    {
        if (PlayerItems.IsDataOnly(request.ItemNetId))
            return request.ItemNetId;
        if (PlayerItems.IsDataOnly(request.ShellNetId))
            return request.ShellNetId;
        LiveItems.LiveItem record = request.Action switch
        {
            ItemAction.MoveSlot => LiveItems.GetAll().FirstOrDefault(other =>
                other.NetId != request.ItemNetId && other.Placement == ItemPlacementKind.PlayerInventory &&
                other.Holder == player.Guid && other.Slot == request.Slot),
            ItemAction.Retrieve when ItemHandles.TryResolve(request.ItemHandle, out Guid id) =>
                Retrieval.Subject(player, id),
            _ => null
        };
        return PlayerItems.IsDataOnly(record) ? record.NetId : (ushort)0;
    }

    private void CompleteItemRequest(ServerPlayer player, ClientboundItemResultPacket result)
    {
        player.ItemRequests.Complete(result.RequestId, result);
        if (!result.Accepted)
            DebugRuntime.Publish("item", "item.request-refused", DebugRuntimeSide.Server,
                DebugSeverity.Warning, "Item", result.ItemNetId.ToString(), new()
                {
                    ["playerId"] = player.PlayerId,
                    ["requestId"] = result.RequestId,
                    ["refusal"] = result.Refusal.ToString(),
                    ["detail"] = result.Detail ?? string.Empty
                });
        SendItemResult(player, result);
    }

    private void SendItemResult(ServerPlayer player, ClientboundItemResultPacket result)
    {
        if (player?.Peer != null && result != null)
            SendPacket(player.Peer, result, DeliveryMethod.ReliableOrdered);
    }

    private static ClientboundItemResultPacket Accept(ItemRequest request, uint revision, ushort netId) => new()
    {
        RequestId = request.RequestId,
        Revision = revision,
        ItemNetId = netId
    };

    private static ClientboundItemResultPacket Refuse(ItemRequest request, string detail,
        ItemRefusal refusal = ItemRefusal.Refused) => new()
    {
        RequestId = request.RequestId,
        Refusal = refusal == ItemRefusal.Refused ? ItemRefusals.Classify(detail) : refusal,
        Detail = detail ?? string.Empty,
        ItemNetId = request.ItemNetId
    };

    private static uint RevisionOf(ushort netId) =>
        LiveItems.TryGet(netId, out LiveItems.LiveItem record) ? record.Revision : 0;

    // A move or a state change: the change the request asks for, validated and committed as a
    // player's change always was (NetworkedItemServer.Server_ReceiveItemUpdate), which sends it to
    // everyone, or a correction to the requester.
    private static ClientboundItemResultPacket CarryChange(ItemRequest request, ServerPlayer player)
    {
        if (!NetworkedItem.TryGet(request.ItemNetId, out NetworkedItem item) || item == null)
            return Refuse(request, "unknown-network-entity");
        ItemUpdate change = ItemRequestTranslation.ToChange(request, player.PlayerId);
        change.PrefabName = item.Item?.InventorySpecs?.ItemPrefabName ?? string.Empty;
        string refusal = item.Server_ReceiveItemUpdate(change, player);
        if (refusal == null && request.Action == ItemAction.Stash)
            PlayerItems.KeepStashedState(item.NetId, request.SavedState);
        return refusal == null ? Accept(request, RevisionOf(item.NetId), item.NetId) : Refuse(request, refusal);
    }

    // To another slot of the requester's inventory (inventory.md, "Moving between slots"): into an
    // empty slot it's a stash with the slot named, so the slot rules refuse a reserved one; onto
    // another of their items the two swap, as vanilla's inventory does.
    private ClientboundItemResultPacket CarryMoveSlot(ItemRequest request, ServerPlayer player)
    {
        if (!NetworkedItem.TryGet(request.ItemNetId, out NetworkedItem item) || item == null)
            return Refuse(request, "unknown-network-entity");
        if (!global::Multiplayer.Core.Items.Inventory.InventorySlots.IsValid(request.Slot))
            return Refuse(request, "slot-invalid");
        LiveItems.LiveItem occupant = LiveItems.GetAll().FirstOrDefault(record =>
            record.NetId != item.NetId && record.Placement == ItemPlacementKind.PlayerInventory &&
            record.Holder == player.Guid && record.Slot == request.Slot);
        if (occupant == null)
            return CarryChange(request, player);
        if (!NetworkedItem.TryGet(occupant.NetId, out NetworkedItem other) || other == null)
            return Refuse(request, "unknown-network-entity");
        bool accepted = LiveItems.TrySwapSlots(item, other, player, request.ExpectedRevision,
            out ItemUpdate first, out ItemUpdate second, out string reason);
        Log($"[Inventory] P{player.PlayerId} swapped item {item.NetId} into slot {request.Slot} with item " +
            $"{other.NetId}: {(accepted ? "done" : "refused, " + reason)}");
        if (!accepted)
        {
            // The requester's game swapped them already: both go back (inventory.md, "Optimistic").
            item.SendCanonicalRejectionCorrection(player, reason);
            other.SendCanonicalRejectionCorrection(player, reason);
            return Refuse(request, reason);
        }
        foreach ((NetworkedItem changed, ItemUpdate snapshot) in new[] { (item, first), (other, second) })
        {
            changed.ApplyServerCanonicalSnapshot(snapshot);
            // The requester's game made this swap; it applies the other item's update like everyone.
            snapshot.OriginatingPlayerId = changed == item ? player.PlayerId : (byte)0;
            SendItemUpdate(snapshot);
        }
        return Accept(request, first.AuthorityRevision, item.NetId);
    }

    // An owner's recall, in one step (wire.md, "Recall"): the server commits it, and the update it
    // sends everyone empties the holder's hand and puts the item in the owner's claimed slot. From
    // Lost and Found it's a collection into that slot.
    private ClientboundItemResultPacket CarryRecall(ItemRequest request, ServerPlayer player)
    {
        if (!NetworkedItem.TryGet(request.ItemNetId, out NetworkedItem item) || item == null)
            return Refuse(request, "unknown-network-entity");
        bool lost = LostAndFoundServer.Contains(item.NetId);
        bool accepted = lost
            ? LostAndFoundServer.TryRetrieveForRecall(player, item, request.ExpectedRevision, request.Slot,
                out ItemUpdate snapshot, out string reason)
            : LiveItems.TryRecall(item, player, request.Slot, request.ExpectedRevision, out snapshot, out reason);
        Log($"[Recall] P{player.PlayerId} recalled item {item.NetId} to slot {request.Slot}" +
            (lost ? " from Lost and Found" : string.Empty) + $": {(accepted ? "done" : "refused, " + reason)}");
        if (!accepted)
            return Refuse(request, reason);
        if (!lost)
            item.ApplyServerCanonicalSnapshot(snapshot);
        // No game did this move: the requester's applies it like everyone's.
        snapshot.OriginatingPlayerId = 0;
        SendItemUpdate(snapshot);
        return Accept(request, snapshot.AuthorityRevision, item.NetId);
    }

    // An owner takes an item back into their Lost and Found (owned-items.md, PR 6b), then gets a fresh list.
    private ClientboundItemResultPacket CarryRetrieve(ItemRequest request, ServerPlayer player)
    {
        string reason = Retrieval.Unknown;
        bool accepted = ItemHandles.TryResolve(request.ItemHandle, out Guid itemId) &&
                        Retrieval.Retrieve(player, itemId, out reason);
        Log($"{player.Username} retrieved owned item {request.ItemHandle}: {(accepted ? "to Lost and Found" : "refused, " + reason)}");
        SendOwnedList(player);
        return accepted ? Accept(request, 0, 0) : Refuse(request, reason);
    }

    // A Lost and Found row into the requester's inventory. A refusal can mean the list was stale,
    // so it's sent again; an accepted one sends it from the collection itself.
    private ClientboundItemResultPacket CarryCollectLost(ItemRequest request, ServerPlayer player)
    {
        ItemUpdate snapshot = null;
        string reason = "unknown-lost-item";
        bool accepted = ItemHandles.TryResolve(request.ItemHandle, out Guid itemId) &&
            LostAndFoundServer.TryCollect(player, itemId, request.ExpectedRevision, request.Slot,
                request.InventoryCapacity, request.OccupiedSlots, out snapshot, out reason);
        Log($"[LostAndFound] P{player.PlayerId} collected row {request.ItemHandle} (revision " +
            $"{request.ExpectedRevision}, slot {request.Slot}): {(accepted ? "done" : "refused, " + reason)}");
        if (!accepted)
        {
            SendLostAndFoundList(player);
            return Refuse(request, reason);
        }
        SendItemUpdate(snapshot);
        return Accept(request, snapshot.AuthorityRevision, snapshot.ItemNetId);
    }

    private static ClientboundItemResultPacket CarryDepositMoney(ItemRequest request, ServerPlayer player)
    {
        if (!NetworkServer.Instance.TrySpendMoneyItem(player, request.ItemNetId, "into the wallet", out double amount))
            return Refuse(request, "money-not-spendable", ItemRefusal.NotAllowed);
        if (amount > double.Epsilon)
            Inventory.Instance.AddMoney(amount); // MoneyChanged broadcasts the new balance
        return Accept(request, 0, request.ItemNetId);
    }

    private static ClientboundItemResultPacket CarryBelowMapReport(ItemRequest request, ServerPlayer player)
    {
        if (float.IsNaN(request.SurfaceHeight) || float.IsInfinity(request.SurfaceHeight))
            return Refuse(request, "surface-height-invalid", ItemRefusal.Invalid);
        ServerItemManager.Instance?.HandleBelowMapReport(player, request.ItemNetId, request.SurfaceHeight);
        return Accept(request, RevisionOf(request.ItemNetId), request.ItemNetId);
    }

    /// <summary>
    /// Spends a banknote or coin <paramref name="player"/> may spend: all its value, and the item
    /// goes. False if it isn't live money (already spent: two players grabbed the same note), or it's
    /// in Lost and Found, a container, or another player's hands or inventory.
    /// </summary>
    internal bool TrySpendMoneyItem(ServerPlayer player, ushort netId, string where, out double amount)
    {
        amount = 0;
        if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null ||
            !item.TryGetComponent(out IMoney money) || !money.ShouldDestroyOnUse)
        {
            LogDebug(() => $"Money from P{player.PlayerId} ignored: netId {netId} is not live money");
            return false;
        }

        if (LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record) &&
            (record.Placement is ItemPlacementKind.LostAndFound or ItemPlacementKind.Container ||
             record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory &&
             record.Holder != player.Guid))
        {
            LogWarning($"Money from P{player.PlayerId} rejected: netId {item.NetId} is {record.Placement} " +
                $"(player {ItemChangeLog.PlayerName(record.Holder)})");
            return false;
        }

        amount = money.TrySpend(money.Amount);
        Log($"P{player.PlayerId} spent ${amount:N2} from money item {item.NetId} {where}");
        item.NoteRemovalCause(ItemChangeLog.Removal(ItemRemovalReason.MoneyDeposited, player, $"${amount:N2} {where}"));
        UnityEngine.Object.Destroy(item.gameObject); // NetworkedItem.OnDestroy replicates the destroy
        return true;
    }

    // ---- Containers: a request names a container or a stored item by its handle, and the
    // container graph by its own session handle; the translation is here, on the server only. ----

    // The graph's handle for the container a wire handle names: the container's own, which is a
    // stored container item's too (a toolbox in a crate), since a container has its shell's id. 0 if neither.
    private static uint ContainerGraphHandle(uint handle) =>
        ItemHandles.TryResolve(handle, out Guid id) && ContainerServer.Graph.TryGetContainer(id, out ContainerRecord container)
            ? container.SessionHandle : 0;

    // Whether a box is stored directly in another: a box's shell has the box's id.
    private static bool InsideOf(Guid box, Guid outer) =>
        ContainerServer.Graph.TryGetItem(box, out StoredItemRecord shell) && shell.ParentContainerId == outer;

    // A stored item a wire handle names, and the graph's handle for the container it's in.
    private static bool TryGetStoredItem(uint handle, out StoredItemRecord stored, out uint containerGraphHandle)
    {
        containerGraphHandle = 0;
        stored = null;
        if (!ItemHandles.TryResolve(handle, out Guid id) || !ContainerServer.Graph.TryGetItem(id, out stored) ||
            !ContainerServer.Graph.TryGetContainer(stored.ParentContainerId, out ContainerRecord container))
            return false;
        containerGraphHandle = container.SessionHandle;
        return true;
    }

    private static ClientboundItemResultPacket ContainerResult(ItemRequest request, ContainerOperationResult result,
        uint revision, ushort netId) =>
        result.Accepted ? Accept(request, revision, netId) : Refuse(request, result.Reason, RefusalOf(result));

    private static ItemRefusal RefusalOf(ContainerOperationResult result) => result.Status switch
    {
        ContainerOperationStatus.Accepted => ItemRefusal.None,
        ContainerOperationStatus.Unauthorized => ItemRefusal.NotYours,
        ContainerOperationStatus.NotFound => ItemRefusal.UnknownItem,
        ContainerOperationStatus.StaleRevision => ItemRefusal.StaleRevision,
        ContainerOperationStatus.SlotOccupied or ContainerOperationStatus.OwnerQuotaExceeded or
            ContainerOperationStatus.DepthExceeded or ContainerOperationStatus.NodeBudgetExceeded => ItemRefusal.NoRoom,
        ContainerOperationStatus.Incompatible or ContainerOperationStatus.CycleDetected => ItemRefusal.NotAllowed,
        ContainerOperationStatus.OperationConflict => ItemRefusal.Busy,
        ContainerOperationStatus.SlotOutOfRange or ContainerOperationStatus.InvalidRecord => ItemRefusal.Invalid,
        _ => ItemRefusal.Refused
    };

    // A live item into a container: the open one's shell, or a container row. Its expected
    // revision is the container's, as its list showed it.
    private static ClientboundItemResultPacket CarryDeposit(ItemRequest request, ServerPlayer player)
    {
        uint container = request.Container != 0 ? ContainerGraphHandle(request.Container) : 0;
        if (request.Container != 0 && container == 0)
            return Refuse(request, "container-handle-not-found", ItemRefusal.UnknownItem);
        ContainerOperationResult result = ContainerServer.Deposit(player, Guid.NewGuid(), request.ShellNetId,
            container, request.ExpectedRevision, request.Slot, request.ItemNetId);
        return ContainerResult(request, result, result.DestinationRevision, request.ItemNetId);
    }

    // A container row into the requester's inventory, in the slot the server picks: the request's
    // slot isn't used (OI-ACC-04). Its answer is sent when it's done; until then a repeat of the
    // request gets nothing.
    private ClientboundItemResultPacket CarryWithdraw(ItemRequest request, ServerPlayer player)
    {
        if (!TryGetStoredItem(request.ItemHandle, out StoredItemRecord stored, out uint container))
            return Refuse(request, "container-item-not-found", ItemRefusal.UnknownItem);
        CoroutineManager.Instance.StartCoroutine(ContainerServer.Withdraw(player, Guid.NewGuid(), container,
            request.ExpectedRevision, stored.Slot,
            (result, withdrawnNetId) => CompleteItemRequest(player,
                ContainerResult(request, result, result.SourceRevision, withdrawnNetId))));
        return null;
    }

    // A container row to another slot, of its own container or another one.
    private static ClientboundItemResultPacket CarryMoveInContainer(ItemRequest request, ServerPlayer player)
    {
        if (!TryGetStoredItem(request.ItemHandle, out StoredItemRecord stored, out uint source))
            return Refuse(request, "container-item-not-found", ItemRefusal.UnknownItem);
        uint destination = ContainerGraphHandle(request.Container);
        if (destination == 0)
            return Refuse(request, "container-handle-not-found", ItemRefusal.UnknownItem);
        // The client has one box open, and both revisions it sends are that box's. Between that box
        // and one inside it (or the one around it) the source isn't the box it has open: the handle
        // names the row exactly, so the source is checked by the row, and the open box by its
        // revision (OI-CON-13).
        uint expectedSource = source != destination && ContainerServer.Graph.TryGetContainer(source, out ContainerRecord from) &&
                              ContainerServer.Graph.TryGetContainer(destination, out ContainerRecord to) &&
                              (InsideOf(to.PersistentContainerId, from.PersistentContainerId) ||
                               InsideOf(from.PersistentContainerId, to.PersistentContainerId))
            ? from.Revision : request.ExpectedRevision;
        ContainerOperationResult result = ContainerServer.Move(player, Guid.NewGuid(), source, expectedSource,
            stored.Slot, destination, request.ContainerRevision, request.Slot);
        return ContainerResult(request, result, result.DestinationRevision, 0);
    }

    // ---- Stashed boomboxes (docs/design/items/inventory.md, "Stashed items that act") ----

    // Its holder only: a report on anyone else's item, or one that isn't a stand-in, is dropped.
    private void HandleStashedCassette(ServerboundStashedCassettePacket packet, ServerPlayer player) =>
        PlayerItems.KeepCassettePosition(player, packet.NetId, packet.Track, packet.Seek, packet.Ended);

    internal void SendStashedSound(ServerPlayer player, ClientboundStashedSoundPacket packet) =>
        SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);

    // ---- Lists (docs/design/items/wire.md, "One list") ----

    private void OnServerboundItemListRequestPacket(ServerboundItemListRequestPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HandleItemListRequest(packet, player);
    }

    private void HandleItemListRequest(ServerboundItemListRequestPacket packet, ServerPlayer player)
    {
        if (packet == null || player?.Peer == null)
            return;
        switch (packet.Kind)
        {
            case ItemListKind.LostAndFound:
                SendLostAndFoundList(player, packet.RequestId);
                break;
            case ItemListKind.Owned:
                SendOwnedList(player, packet.RequestId);
                break;
            case ItemListKind.Container:
                // A shell kept as data on the server (OI-COLD-06) is made again before it's opened.
                PlayerItems.WithObject(packet.ShellNetId, _ =>
                {
                    if (player.Peer != null)
                        SendPacket(player.Peer, ContainerList(packet, player), DeliveryMethod.ReliableOrdered);
                });
                break;
            default:
                SendPacket(player.Peer, new ClientboundItemListPacket
                {
                    RequestId = packet.RequestId,
                    Kind = packet.Kind,
                    Refusal = ItemRefusal.Invalid,
                    Detail = "item-list-kind-invalid"
                }, DeliveryMethod.ReliableOrdered);
                break;
        }
    }

    /// <summary>
    /// A player's Lost and Found, oldest first: for their request, and unasked (request 0)
    /// whenever it changes.
    /// </summary>
    public void SendLostAndFoundList(ServerPlayer player, uint requestId = 0)
    {
        if (player?.Peer == null) return;
        SendPacket(player.Peer, new ClientboundItemListPacket
        {
            RequestId = requestId,
            Kind = ItemListKind.LostAndFound,
            Rows = LostAndFoundServer.RowsFor(player)
        }, DeliveryMethod.ReliableOrdered);
    }

    // The player's owned-items list (owned-items.md, PR 6a), worded here for every game alike.
    private void SendOwnedList(ServerPlayer player, uint requestId = 0)
    {
        if (player?.Peer == null) return;
        SendPacket(player.Peer, new ClientboundItemListPacket
        {
            RequestId = requestId,
            Kind = ItemListKind.Owned,
            Rows = OwnedItemRows.For(player).Select(row => new ItemRow
            {
                Handle = row.Handle,
                PrefabName = row.Prefab ?? string.Empty,
                DisplayName = row.DisplayName ?? string.Empty,
                Where = row.Where ?? string.Empty,
                Flags = row.Retrievable ? ItemRowFlags.Retrievable : ItemRowFlags.None
            }).ToArray()
        }, DeliveryMethod.ReliableOrdered);
    }

    // A cold container's slots, opened by its shell's NetId or by a handle (a container, or a
    // stored container item). Each row is its item's handle; a container item's opens it.
    private static ClientboundItemListPacket ContainerList(ServerboundItemListRequestPacket packet, ServerPlayer player)
    {
        ContainerView view = null;
        ContainerOperationResult result;
        if (packet.ShellNetId != 0)
            result = ContainerServer.Browse(player, packet.ShellNetId, 0, ContainerGraphLimit, out view);
        else
        {
            uint handle = ContainerGraphHandle(packet.Container);
            result = handle == 0
                ? new ContainerOperationResult { Kind = ContainerOperationKind.Browse, Status = ContainerOperationStatus.NotFound, Reason = "container-handle-not-found" }
                : ContainerServer.Browse(player, handle, 0, ContainerGraphLimit, out view);
        }
        ClientboundItemListPacket list = new()
        {
            RequestId = packet.RequestId,
            Kind = ItemListKind.Container,
            Refusal = RefusalOf(result),
            Detail = result.Accepted ? string.Empty : result.Reason ?? string.Empty
        };
        if (!result.Accepted || view == null)
            return list;
        ContainerGraph graph = ContainerServer.Graph;
        list.Container = graph.TryGetContainer(view.ContainerHandle, out ContainerRecord container)
            ? ItemHandles.Of(container.PersistentContainerId) : 0;
        list.Revision = view.Revision;
        list.Capacity = view.Capacity;
        list.Rows = view.Slots.Select(slot => new ItemRow
        {
            Handle = graph.TryGetItem(slot.ItemHandle, out StoredItemRecord stored) ? ItemHandles.Of(stored.PersistentItemId) : 0,
            PrefabName = slot.PrefabName ?? string.Empty,
            DisplayName = slot.DisplayName ?? string.Empty,
            Slot = slot.Slot,
            Revision = view.Revision,
            Flags = (slot.ForeignOwned ? ItemRowFlags.SomeoneElses : ItemRowFlags.None) |
                    (slot.IsContainer ? ItemRowFlags.Container : ItemRowFlags.None)
        }).ToArray();
        return list;
    }

    // The graph's own bound on one view: a container holds no more than this.
    private const int ContainerGraphLimit = 64;
}
