using DV.InventorySystem;
using LiteNetLib;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Containers;
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
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Transport;
using Multiplayer.Debugging.Protocol;
using ItemMoveReason = Multiplayer.Core.Items.ChangeCauses.ItemMoveReason;
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

    bool IItemServer.TryGetPersistentOwner(ushort itemNetId, out byte ownerPlayerId)
    {
        bool known = LiveItems.TryGet(itemNetId, out LiveItems.LiveItem record);
        ownerPlayerId = known ? record.PersistentOwnerPlayerId : (byte)0;
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

    bool IItemServer.OwnsWorldPersistence(NetworkedItem item) => ItemSaveLoad.OwnsWorldPersistence(item);

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

    public void SendItemsBulkUpdatePacket(List<ItemUpdateData> items, ServerPlayer player)
    {
        // Item packets go out many times a second: they're traced (the debug system), not logged.
        DebugRuntime.Publish("item", "item.bulk-send-requested", DebugRuntimeSide.Server, data: new()
        {
            ["count"] = items?.Count ?? 0,
            ["playerId"] = player?.PlayerId ?? 0,
            ["itemNetIds"] = string.Join(",", items?.Select(item => item?.ItemNetId ?? 0) ?? Enumerable.Empty<ushort>())
        });

        var packet = new CommonItemsBulkUpdatePacket { Items = items };

        if (player == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForItems, excludeSelf: true);
        else
        {
            TraceItemDeliveryExpected(packet, player.Peer, DeliveryMethod.ReliableOrdered);
            SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
        }
    }

    public void SendItemUpdatePacket(ItemUpdateData item, ServerPlayer sendToPlayer = null, ServerPlayer excludePlayer = null)
    {
        DebugRuntime.Publish("item", "item.relay-requested", DebugRuntimeSide.Server, entityType: "Item", entityId: item?.ItemNetId.ToString(),
            data: Merge(DebugTrace.ItemSnapshotData(item), new() { ["sendToPlayer"] = sendToPlayer?.PlayerId ?? 0, ["excludePlayer"] = excludePlayer?.PlayerId ?? 0 }));

        var packet = new CommonItemUpdatePacket { ItemData = item };
        if (sendToPlayer == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForItems, excludePlayer?.Peer, excludeSelf: true);
        else
        {
            TraceItemDeliveryExpected(packet, sendToPlayer.Peer, DeliveryMethod.ReliableOrdered);
            SendPacket(sendToPlayer.Peer, packet, DeliveryMethod.ReliableOrdered);
        }
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

    public void SendLostItemsSnapshot(ServerPlayer player, uint requestId = 0)
    {
        if (player?.Peer == null) return;
        LostItemData[] items = LostAndFoundServer.SnapshotFor(player);
        SendPacket(player.Peer, new ClientboundLostItemsSnapshotPacket
        {
            RequestId = requestId,
            Generation = LostAndFoundServer.Generation,
            Handles = items.Select(item => item.Handle).ToArray(),
            NetIds = items.Select(item => item.NetId).ToArray(),
            Revisions = items.Select(item => item.Revision).ToArray(),
            PrefabNames = items.Select(item => item.PrefabName ?? string.Empty).ToArray(),
            DisplayNames = items.Select(item => item.DisplayName ?? string.Empty).ToArray(),
            Reasons = items.Select(item => item.Reason).ToArray(),
            LostUtcTicks = items.Select(item => item.LostUtcTicks).ToArray()
        }, DeliveryMethod.ReliableOrdered);
    }

    private void TraceItemDeliveryExpected<T>(T packet, ITransportPeer peer, DeliveryMethod deliveryMethod)
    {
        if (!DebugRuntime.EnabledFor("item") || peer == null || !TryGetServerPlayer(peer, out ServerPlayer recipient)) return;
        IEnumerable<ItemUpdateData> snapshots = packet switch
        {
            CommonItemUpdatePacket single when single.ItemData != null => new[] { single.ItemData },
            CommonItemsBulkUpdatePacket bulk when bulk.Items != null => bulk.Items.Where(item => item != null),
            _ => Array.Empty<ItemUpdateData>()
        };
        foreach (ItemUpdateData snapshot in snapshots)
        {
            Dictionary<string, object> data = DebugTrace.ItemSnapshotData(snapshot);
            data["packetType"] = packet.GetType().Name;
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

    //private void OnCommonItemChangePacket(CommonItemsBulkUpdatePacket packet, ITransportPeer peer)
    //{
    //    if (!TryGetServerPlayer(peer, out var player))
    //        return;

    //    LogDebug(() => $"OnCommonItemChangePacket({packet?.Items?.Count}, {peer.Id} (\"{player.Username}\"))");

    //    LogDebug(() =>
    //    {
    //        string debug = "";

    //        foreach (var item in packet?.Items)
    //        {
    //            debug += "UpdateType: " + item?.UpdateType + "\r\n";
    //            debug += "itemNetId: " + item?.ItemNetId + "\r\n";
    //            debug += "PrefabName: " + item?.PrefabName + "\r\n";
    //            debug += "Equipped: " + item?.ItemState + "\r\n";
    //            debug += "Position: " + item?.ItemPosition + "\r\n";
    //            debug += "Rotation: " + item?.ItemRotation + "\r\n";
    //            debug += "ThrowDirection: " + item?.ThrowDirection + "\r\n";
    //            debug += "Player: " + player.PlayerId + "\r\n";
    //            debug += "CarNetId: " + item?.CarNetId + "\r\n";
    //            debug += "AttachedFront: " + item?.AttachedFront + "\r\n";

    //            debug += "States:";

    //            if (item.States != null)
    //                foreach (var state in item?.States)
    //                    debug += "\r\n\t" + state.Key + ": " + state.Value;
    //        }

    //        return debug;
    //    }

    //    );

    //    // Set player id for all items, do not trust the client to send the correct player id
    //    foreach (var item in packet.Items)
    //        item.Player = player.PlayerId;

    //    NetworkedItemManager.Instance.ReceiveSnapshots(packet.Items);
    //}

    private void OnCommonItemUpdatePacket(CommonItemUpdatePacket packet, ITransportPeer peer)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Server, "Item", packet?.ItemData?.ItemNetId.ToString());
        if (!TryGetServerPlayer(peer, out var player))
        {
            DebugTrace.Validation("item", "Item", packet?.ItemData?.ItemNetId.ToString(), false, "unknown-sender", DebugRuntimeSide.Server);
            return;
        }

        LogDebug(() => $"OnCommonItemUpdatePacket({packet?.ItemData.ItemNetId}, [{peer.Id}, {player.Username}])");
        if (packet?.ItemData == null)
        {
            DebugTrace.Validation("item", "Item", string.Empty, false, "missing-item-data", DebugRuntimeSide.Server);
            return;
        }
        // Runtime creation is an explicit host spawn operation or the narrowly fenced TEMPORARY
        // compatibility-adoption bridge. A normal state packet may never cause a client-authored
        // logical entity to appear on the host.
        if (packet.ItemData.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create))
        {
            DebugTrace.Validation("item", "Item", packet.ItemData.ItemNetId.ToString(), false,
                "client-create-not-permitted", DebugRuntimeSide.Server);
            return;
        }
        // Set player id for all items, do not trust the client to send the correct player id

        packet.ItemData.PlayerId = player.PlayerId;
        if (NetworkedItem.TryGet(packet.ItemData.ItemNetId, out var networkedItem))
        {
            networkedItem.Server_ReceiveItemUpdate(packet.ItemData, player);
        }
        else
        {
            DebugTrace.Validation("item", "Item", packet.ItemData.ItemNetId.ToString(), false, "unknown-network-entity", DebugRuntimeSide.Server);
        }
    }

    private void OnServerboundItemBelowMapPacket(ServerboundItemBelowMapPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player) &&
            !float.IsNaN(packet.SurfaceHeight) && !float.IsInfinity(packet.SurfaceHeight))
            ServerItemManager.Instance?.HandleBelowMapReport(player, packet.ItemNetId, packet.SurfaceHeight);
    }

    private void OnServerboundMoneyItemDepositPacket(ServerboundMoneyItemDepositPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!TrySpendMoneyItem(player, packet.ItemNetId, "into the wallet", out double amount))
            return;
        if (amount > double.Epsilon)
            Inventory.Instance.AddMoney(amount); // MoneyChanged broadcasts the new balance
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
             record.PlacementPlayerId != player.PlayerId))
        {
            LogWarning($"Money from P{player.PlayerId} rejected: netId {item.NetId} is {record.Placement} " +
                $"(player {record.PlacementPlayerId})");
            return false;
        }

        amount = money.TrySpend(money.Amount);
        Log($"P{player.PlayerId} spent ${amount:N2} from money item {item.NetId} {where}");
        item.NoteRemovalCause(ItemChangeLog.Removal(ItemRemovalReason.MoneyDeposited, player, $"${amount:N2} {where}"));
        UnityEngine.Object.Destroy(item.gameObject); // NetworkedItem.OnDestroy replicates the destroy
        return true;
    }

    private void OnServerboundItemRecallPacket(ServerboundItemRecallPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            LogWarning($"[LostAndFound Recall] Recall packet has no authenticated player: " +
                $"peer={peer?.Id}, netId={packet.ItemNetId}");
            return;
        }

        Log($"[LostAndFound Recall] Recall packet received: player=P{player.PlayerId}, " +
            $"netId={packet.ItemNetId}, revision={packet.ExpectedRevision}, slot={packet.RequestedSlot}");

        bool accepted = false;
        bool storedInLostAndFound = false;
        string rejectionReason = string.Empty;
        ItemUpdateData snapshot = null;
        if (!NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem item) || item == null)
            rejectionReason = "unknown-network-entity";
        else if (storedInLostAndFound = LostAndFoundServer.Contains(item.NetId))
            accepted = LostAndFoundServer.TryRetrieveForRecall(player, item,
                packet.ExpectedRevision, packet.RequestedSlot, out snapshot, out rejectionReason);
        else if (pendingItemRecallByItem.ContainsKey(item.NetId))
            rejectionReason = "recall-already-pending";
        else if (LiveItems.TryPrepareRecall(item, player, packet.RequestedSlot,
                     packet.ExpectedRevision, out snapshot, out rejectionReason))
        {
            uint operationId = NextItemRecallOperationId();
            PendingItemRecall pending = new(operationId, item.NetId, packet.ExpectedRevision,
                packet.RequestedSlot, unchecked(NetworkLifecycle.Instance.Tick + ItemRecallTimeoutTicks),
                player, peer);
            pendingItemRecalls.Add(operationId, pending);
            pendingItemRecallByItem.Add(item.NetId, operationId);
            SendPacket(peer, new ClientboundItemRecallPreparePacket
            {
                OperationId = operationId,
                ItemNetId = item.NetId,
                BaseRevision = packet.ExpectedRevision,
                PreparedRevision = snapshot.AuthorityRevision,
                RequestedSlot = packet.RequestedSlot
            }, DeliveryMethod.ReliableOrdered);
            DebugRuntime.Publish("item", "item.recall-prepare-requested", DebugRuntimeSide.Server,
                entityType: "Item", entityId: item.NetId.ToString(), data: new()
                {
                    ["operationId"] = operationId,
                    ["requestingPlayerId"] = player.PlayerId,
                    ["baseRevision"] = packet.ExpectedRevision,
                    ["preparedRevision"] = snapshot.AuthorityRevision,
                    ["requestedSlot"] = packet.RequestedSlot
                });
            return;
        }

        if (accepted)
        {
            if (!storedInLostAndFound)
                item.ApplyServerCanonicalSnapshot(snapshot);
            SendItemUpdatePacket(snapshot);
        }
        else
        {
            DebugRuntime.Publish("item", "item.recall-rejected", DebugRuntimeSide.Server,
                DebugSeverity.Warning, "Item", packet.ItemNetId.ToString(), new()
                {
                    ["requestingPlayerId"] = player.PlayerId,
                    ["expectedRevision"] = packet.ExpectedRevision,
                    ["requestedSlot"] = packet.RequestedSlot,
                    ["rejectionReason"] = rejectionReason ?? string.Empty
                });
        }

        SendPacket(peer, new ClientboundItemRecallResultPacket
        {
            OperationId = 0,
            ItemNetId = packet.ItemNetId,
            Accepted = accepted,
            AuthorityRevision = snapshot?.AuthorityRevision ?? 0,
            RejectionReason = rejectionReason ?? string.Empty
        }, DeliveryMethod.ReliableOrdered);
        Log($"[LostAndFound Recall] Recall result sent: player=P{player.PlayerId}, " +
            $"netId={packet.ItemNetId}, stored={storedInLostAndFound}, accepted={accepted}, " +
            $"revision={snapshot?.AuthorityRevision ?? 0}, reason={rejectionReason ?? string.Empty}");
    }

    private void OnServerboundItemRecallPreparedPacket(ServerboundItemRecallPreparedPacket packet,
        ITransportPeer peer)
    {
        if (!pendingItemRecalls.TryGetValue(packet.OperationId, out PendingItemRecall pending) ||
            pending.Peer != peer || pending.ItemNetId != packet.ItemNetId)
            return;
        if (!packet.Succeeded)
        {
            CancelPendingItemRecall(pending, string.IsNullOrWhiteSpace(packet.FailureReason)
                ? "client-recall-prepare-failed" : packet.FailureReason, true);
            return;
        }
        string rejectionReason = string.Empty;
        if (!NetworkedItem.TryGet(pending.ItemNetId, out NetworkedItem item) || item == null ||
            !LiveItems.TryCommitPreparedRecall(item, pending.Player,
                pending.RequestedSlot, pending.BaseRevision, out ItemUpdateData snapshot,
                out rejectionReason))
        {
            CancelPendingItemRecall(pending, rejectionReason ?? "recall-commit-failed", true);
            return;
        }

        RemovePendingItemRecall(pending);
        item.ApplyServerCanonicalSnapshot(snapshot);
        SendItemUpdatePacket(snapshot);
        SendPacket(peer, new ClientboundItemRecallResultPacket
        {
            OperationId = pending.OperationId,
            ItemNetId = item.NetId,
            Accepted = true,
            AuthorityRevision = snapshot.AuthorityRevision,
            RejectionReason = string.Empty
        }, DeliveryMethod.ReliableOrdered);
    }

    private void CancelPendingItemRecall(PendingItemRecall pending, string reason, bool notifyClient)
    {
        RemovePendingItemRecall(pending);
        uint revision = 0;
        if (NetworkedItem.TryGet(pending.ItemNetId, out NetworkedItem item) && item != null &&
            LiveItems.TryCreateCorrectionSnapshot(item, out ItemUpdateData correction,
                ItemChangeLog.Move(ItemMoveReason.HostCorrection, detail: "recall cancelled: " + reason)))
        {
            revision = correction.AuthorityRevision;
            if (notifyClient)
                SendItemUpdatePacket(correction, pending.Player);
        }
        DebugRuntime.Publish("item", reason == "recall-prepare-timeout"
                ? "item.recall-transaction-timeout" : "item.recall-transaction-cancelled",
            DebugRuntimeSide.Server, DebugSeverity.Warning, "Item", pending.ItemNetId.ToString(), new()
            {
                ["operationId"] = pending.OperationId,
                ["requestingPlayerId"] = pending.Player?.PlayerId ?? 0,
                ["baseRevision"] = pending.BaseRevision,
                ["rejectionReason"] = reason ?? string.Empty
            });
        if (notifyClient && pending.Peer != null)
            SendPacket(pending.Peer, new ClientboundItemRecallResultPacket
            {
                OperationId = pending.OperationId,
                ItemNetId = pending.ItemNetId,
                Accepted = false,
                AuthorityRevision = revision,
                RejectionReason = reason ?? string.Empty
            }, DeliveryMethod.ReliableOrdered);
    }

    private void RemovePendingItemRecall(PendingItemRecall pending)
    {
        pendingItemRecalls.Remove(pending.OperationId);
        pendingItemRecallByItem.Remove(pending.ItemNetId);
    }

    private uint NextItemRecallOperationId()
    {
        do nextItemRecallOperationId++;
        while (nextItemRecallOperationId == 0 || pendingItemRecalls.ContainsKey(nextItemRecallOperationId));
        return nextItemRecallOperationId;
    }

    // The Lost and Found screen's and the container UI's requests. The host's own player's are
    // handled in-process (RegisterLocalItems); the answers go to its peer like anyone's, and its
    // session shows them (LocalPlayerSession.Items).
    partial void RegisterLocalItems()
    {
        HandleLocal<ServerboundLostItemsRequestPacket>(HandleLostItemsRequest);
        HandleLocal<ServerboundOwnedItemsRequestPacket>(HandleOwnedItemsRequest);
        HandleLocal<ServerboundOwnedItemRetrievePacket>(HandleOwnedItemRetrieve);
        HandleLocal<ServerboundLostItemRetrievePacket>(HandleLostItemRetrieve);
        HandleLocal<ServerboundContainerBrowsePacket>(HandleContainerBrowse);
        HandleLocal<ServerboundContainerMutationPacket>(HandleContainerMutation);
    }

    private void OnServerboundLostItemsRequestPacket(ServerboundLostItemsRequestPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HandleLostItemsRequest(packet, player);
    }

    private void HandleLostItemsRequest(ServerboundLostItemsRequestPacket packet, ServerPlayer player) =>
        SendLostItemsSnapshot(player, packet.RequestId);

    private void OnServerboundOwnedItemsRequestPacket(ServerboundOwnedItemsRequestPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HandleOwnedItemsRequest(packet, player);
    }

    private void OnServerboundOwnedItemRetrievePacket(ServerboundOwnedItemRetrievePacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HandleOwnedItemRetrieve(packet, player);
    }

    // An owner takes an item back into their Lost and Found (owned-items.md, PR 6b), then gets a fresh list.
    private void HandleOwnedItemRetrieve(ServerboundOwnedItemRetrievePacket packet, ServerPlayer player)
    {
        if (player?.Peer == null) return;
        string reason = Retrieval.Unknown;
        bool accepted = OwnedItemRows.TryResolve(packet.Handle, out Guid itemId) &&
                        Retrieval.Retrieve(player, itemId, out reason);
        Log($"{player.Username} retrieved owned item {packet.Handle}: {(accepted ? "to Lost and Found" : "refused, " + reason)}");
        SendPacket(player.Peer, new ClientboundOwnedItemRetrieveResultPacket
        {
            RequestId = packet.RequestId,
            Handle = packet.Handle,
            Accepted = accepted,
            RejectionReason = accepted ? string.Empty : reason ?? string.Empty
        }, DeliveryMethod.ReliableOrdered);
        HandleOwnedItemsRequest(new ServerboundOwnedItemsRequestPacket { RequestId = packet.RequestId }, player);
    }

    // The player's owned-items list (owned-items.md, PR 6a), worded here for every game alike.
    private void HandleOwnedItemsRequest(ServerboundOwnedItemsRequestPacket packet, ServerPlayer player)
    {
        if (player?.Peer == null) return;
        List<OwnedItemRow> rows = OwnedItemRows.For(player);
        SendPacket(player.Peer, new ClientboundOwnedItemsPacket
        {
            RequestId = packet.RequestId,
            Handles = rows.Select(row => row.Handle).ToArray(),
            PrefabNames = rows.Select(row => row.Prefab).ToArray(),
            DisplayNames = rows.Select(row => row.DisplayName).ToArray(),
            Wheres = rows.Select(row => row.Where).ToArray(),
            Retrievable = rows.Select(row => row.Retrievable).ToArray()
        }, DeliveryMethod.ReliableOrdered);
    }

    private void OnServerboundLostItemRetrievePacket(ServerboundLostItemRetrievePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            LogWarning($"[LostAndFound Recall] Retrieval packet has no authenticated player: " +
                $"peer={peer?.Id}, request={packet.RequestId}, handle={packet.LostHandle}");
            return;
        }
        HandleLostItemRetrieve(packet, player);
    }

    private void HandleLostItemRetrieve(ServerboundLostItemRetrievePacket packet, ServerPlayer player)
    {
        Log($"[LostAndFound Recall] Retrieval packet received: player=P{player.PlayerId}, " +
            $"request={packet.RequestId}, handle={packet.LostHandle}, revision={packet.ExpectedRevision}, " +
            $"requestedSlot={packet.RequestedSlot}, existingSlot={packet.ExistingItemSlot}, " +
            $"capacity={packet.InventoryCapacity}, occupied=[{string.Join(",", packet.OccupiedSlots ?? System.Array.Empty<int>())}]");
        bool accepted = LostAndFoundServer.TryRetrieve(player, packet.LostHandle,
            packet.ExpectedRevision, packet.RequestedSlot, packet.ExistingItemSlot, packet.InventoryCapacity,
            packet.OccupiedSlots, out ItemUpdateData snapshot, out string rejectionReason);
        if (accepted)
            SendItemUpdatePacket(snapshot);
        SendPacket(player.Peer, new ClientboundLostItemRetrieveResultPacket
        {
            RequestId = packet.RequestId,
            LostHandle = packet.LostHandle,
            Accepted = accepted,
            AuthorityRevision = snapshot?.AuthorityRevision ?? 0,
            RejectionReason = rejectionReason ?? string.Empty
        }, DeliveryMethod.ReliableOrdered);
        // Successful retrievals publish the changed owner snapshot at the shared commit point.
        // Rejections can still invalidate a stale registry entry, so retain the explicit refresh.
        if (!accepted)
            SendLostItemsSnapshot(player, packet.RequestId);
        Log($"[LostAndFound Recall] Retrieval result sent: player=P{player.PlayerId}, " +
            $"request={packet.RequestId}, handle={packet.LostHandle}, accepted={accepted}, " +
            $"revision={snapshot?.AuthorityRevision ?? 0}, reason={rejectionReason ?? string.Empty}");
    }

    private void OnServerboundContainerBrowsePacket(ServerboundContainerBrowsePacket packet,
        ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HandleContainerBrowse(packet, player);
    }

    private void HandleContainerBrowse(ServerboundContainerBrowsePacket packet, ServerPlayer player)
    {
        ContainerView view;
        ContainerOperationResult result = packet.ShellNetId != 0
            ? ContainerServer.Browse(player, packet.ShellNetId, packet.Offset,
                packet.Count, out view)
            : ContainerServer.Browse(player, packet.ContainerHandle, packet.Offset,
                packet.Count, out view);
        SendPacket(player.Peer, ProjectContainerView(packet.RequestId, result, view),
            DeliveryMethod.ReliableOrdered);
    }

    private void OnServerboundContainerMutationPacket(ServerboundContainerMutationPacket packet,
        ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HandleContainerMutation(packet, player);
    }

    private void HandleContainerMutation(ServerboundContainerMutationPacket packet, ServerPlayer player)
    {
        ITransportPeer peer = player.Peer;
        Guid operationId = packet.OperationId?.Length == 16
            ? new Guid(packet.OperationId) : Guid.Empty;
        ContainerOperationResult result;
        ushort materializedNetId = 0;
        if (operationId == Guid.Empty)
        {
            result = new ContainerOperationResult
            {
                Kind = (ContainerOperationKind)packet.Kind,
                Status = ContainerOperationStatus.InvalidRecord,
                Reason = "operation-id-invalid"
            };
        }
        else
        {
            switch ((ContainerOperationKind)packet.Kind)
            {
                case ContainerOperationKind.Deposit:
                    result = ContainerServer.Deposit(player, operationId,
                        packet.ShellNetId, packet.DestinationContainerHandle,
                        packet.ExpectedDestinationRevision,
                        packet.DestinationSlot, packet.ItemNetId);
                    break;
                case ContainerOperationKind.Withdraw:
                    CoroutineManager.Instance.StartCoroutine(
                        ContainerServer.Withdraw(player, operationId,
                        packet.SourceContainerHandle, packet.ExpectedSourceRevision,
                        packet.SourceSlot, packet.DestinationSlot,
                        (withdrawalResult, withdrawnNetId) =>
                            SendContainerMutationResult(peer, packet, operationId,
                                withdrawalResult, withdrawnNetId)));
                    return;
                case ContainerOperationKind.Move:
                    result = ContainerServer.Move(player, operationId,
                        packet.SourceContainerHandle, packet.ExpectedSourceRevision,
                        packet.SourceSlot, packet.DestinationContainerHandle,
                        packet.ExpectedDestinationRevision, packet.DestinationSlot);
                    break;
                default:
                    result = new ContainerOperationResult
                    {
                        OperationId = operationId,
                        Kind = (ContainerOperationKind)packet.Kind,
                        Status = ContainerOperationStatus.InvalidRecord,
                        Reason = "container-mutation-kind-invalid"
                    };
                    break;
            }
        }
        SendContainerMutationResult(peer, packet, operationId, result, materializedNetId);
    }

    private void SendContainerMutationResult(ITransportPeer peer,
        ServerboundContainerMutationPacket packet, Guid operationId,
        ContainerOperationResult result, ushort materializedNetId)
    {
        SendPacket(peer, new ClientboundContainerMutationResultPacket
        {
            RequestId = packet.RequestId,
            OperationId = operationId.ToByteArray(),
            Kind = (byte)result.Kind,
            Accepted = result.Accepted,
            Status = (byte)result.Status,
            RejectionReason = result.Reason ?? string.Empty,
            SourceRevision = result.SourceRevision,
            DestinationRevision = result.DestinationRevision,
            MaterializedItemNetId = materializedNetId
        }, DeliveryMethod.ReliableOrdered);
    }

    private static ClientboundContainerViewPacket ProjectContainerView(uint requestId,
        ContainerOperationResult result, ContainerView view)
    {
        ContainerSlotView[] slots = view?.Slots?.ToArray() ?? Array.Empty<ContainerSlotView>();
        return new ClientboundContainerViewPacket
        {
            RequestId = requestId,
            Accepted = result.Accepted,
            RejectionReason = result.Reason ?? string.Empty,
            ContainerHandle = view?.ContainerHandle ?? 0,
            Revision = view?.Revision ?? 0,
            Capacity = view?.Capacity ?? 0,
            Offset = view?.Offset ?? 0,
            HasMore = view?.HasMore ?? false,
            Slots = slots.Select(slot => slot.Slot).ToArray(),
            ItemHandles = slots.Select(slot => slot.ItemHandle).ToArray(),
            PrefabNames = slots.Select(slot => slot.PrefabName ?? string.Empty).ToArray(),
            DisplayNames = slots.Select(slot => slot.DisplayName ?? string.Empty).ToArray(),
            ForeignOwned = slots.Select(slot => slot.ForeignOwned).ToArray(),
            ChildContainerHandles = slots.Select(slot => slot.ChildContainerHandle).ToArray(),
            ChildItemCounts = slots.Select(slot => slot.ChildItemCount).ToArray(),
            StateVersions = slots.Select(slot => slot.StateVersion).ToArray()
        };
    }
}
