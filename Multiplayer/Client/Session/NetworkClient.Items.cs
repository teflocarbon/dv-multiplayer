using LiteNetLib;
using Multiplayer.Client.Items.Inventory;
using Multiplayer.Client.Items.LiveState;
using Multiplayer.Client.Items.LostAndFound;
using Multiplayer.Client.Items.Projection;
using Multiplayer.Client.Items.Requests;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Items;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;

namespace Multiplayer.Client.Session;

// NetworkClient's items handlers and sends (see docs/design/code-layout.md).
public partial class NetworkClient : IItemClient
{
    private void OnClientboundItemUpdatesPacket(ClientboundItemUpdatesPacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client);
        LogDebug(() => $"OnClientboundItemUpdatesPacket({packet?.Updates?.Count})");
        if (packet?.Updates != null)
            ClientItemManager.Instance?.ReceiveSnapshots(packet.Updates);
    }

    private void OnClientboundInventoryRowsPacket(ClientboundInventoryRowsPacket packet)
    {
        LogDebug(() => $"OnClientboundInventoryRowsPacket({packet?.Rows?.Count}, full {packet?.Full})");
        InventoryRows.Receive(packet?.Rows, packet?.States, packet?.Full == true);
        ClientItemManager.Instance?.ReceiveInventoryRows(packet?.Rows, packet?.States, packet?.Full == true);
    }

    private void OnClientboundItemResultPacket(ClientboundItemResultPacket packet)
    {
        ItemExchange.Receive(packet);
        ClientItemManager.Instance?.ReceiveTakeOutResult(packet);
        ItemResultReceived?.Invoke(packet);
    }

    private void OnClientboundItemListPacket(ClientboundItemListPacket packet)
    {
        ItemExchange.Receive(packet);
        ItemListReceived?.Invoke(packet);
    }

    IItemManagerClientPart IItemClient.CreateItemManagerPart(NetworkedItemManager manager) => new ClientItemManager(manager);

    INetworkedItemClientPart IItemClient.CreateItemPart(NetworkedItem item) => new NetworkedItemClient(item);

    IEnumerable<Dictionary<string, object>> IItemClient.LostAndFoundDebugSnapshot() => LostAndFoundList.DebugSnapshot();

    public uint SendItemRequest(ItemRequest request)
    {
        if (request == null || request.ItemNetId == 0 && request.ItemHandle == 0)
        {
            LogWarning($"Blocked an item request naming no item: {request?.Action}");
            DebugRuntime.Publish("item", "item.zero-id-send-blocked", DebugRuntimeSide.Client, DebugSeverity.Error,
                "Item", "0", new() { ["action"] = request?.Action.ToString() ?? string.Empty });
            return 0;
        }
        ItemExchange.Number(request);
        DebugRuntime.Publish("item", "item.request-sent", DebugRuntimeSide.Client, entityType: "Item",
            entityId: request.ItemNetId.ToString(), data: new()
            {
                ["requestId"] = request.RequestId,
                ["action"] = request.Action.ToString(),
                ["itemHandle"] = request.ItemHandle,
                ["expectedRevision"] = request.ExpectedRevision,
                ["slot"] = request.Slot
            });
        SendPacketToServer(new ServerboundItemRequestPacket { Request = request }, DeliveryMethod.ReliableOrdered);
        return request.RequestId;
    }

    public uint RequestItemList(ItemListKind kind, ushort shellNetId = 0, uint container = 0)
    {
        uint requestId = ItemExchange.NextRequestId();
        SendPacketToServer(new ServerboundItemListRequestPacket
        {
            RequestId = requestId,
            Kind = kind,
            ShellNetId = shellNetId,
            Container = container
        }, DeliveryMethod.ReliableOrdered);
        return requestId;
    }

    public void SendWorldItemProjectionAck(ushort netId, uint revision, bool projected)
    {
        SendPacketToServer(new ServerboundWorldItemProjectionAckPacket
        {
            ItemNetId = netId,
            AuthorityRevision = revision,
            Projected = projected
        }, DeliveryMethod.ReliableOrdered);
    }

    private void SendWorldItemCatalogue()
    {
        var summary = NetworkedItemManager.Instance.GetAuthoredCatalogueSummary();
        SendPacketToServer(new ServerboundWorldItemCataloguePacket
        {
            ItemCount = summary.Count,
            CollisionCount = summary.Collisions,
            Digest = summary.Digest
        }, DeliveryMethod.ReliableOrdered);
    }

    private void OnClientboundWorldItemCataloguePacket(ClientboundWorldItemCataloguePacket packet)
    {
        ClientItemManager.Instance?.SetAuthoredCatalogueNegotiationResult(packet?.Accepted == true);
        DebugRuntime.Publish("item-world", "world-item.catalogue-result", DebugRuntimeSide.Client,
            packet?.Accepted == true ? DebugSeverity.Info : DebugSeverity.Error, data: new()
            {
                ["accepted"] = packet?.Accepted == true,
                ["hostCount"] = packet?.HostItemCount ?? 0,
                ["hostCollisionCount"] = packet?.HostCollisionCount ?? 0,
                ["hostDigest"] = packet?.HostDigest ?? string.Empty,
                ["reason"] = packet?.Reason ?? "missing-response"
            });
    }

    private void OnClientboundItemSpatialLeasePacket(ClientboundItemSpatialLeasePacket packet) =>
        ClientItemManager.Instance?.Spatial.ReceiveClientLease(packet);

    private void OnClientboundItemSpatialSamplePacket(ClientboundItemSpatialSamplePacket packet)
    {
        if (packet?.State != null)
            ClientItemManager.Instance?.Spatial.ReceiveRelayedSample(packet.State);
    }

    private void OnClientboundItemSpatialCommitPacket(ClientboundItemSpatialCommitPacket packet) =>
        ClientItemManager.Instance?.Spatial.ReceiveSpatialCommit(packet);

    public void SendItemSpatialSample(ItemSpatialStateData state)
    {
        if (state == null) return;
        SendPacketToServer(new ServerboundItemSpatialSamplePacket { State = state },
            DeliveryMethod.Unreliable);
    }

    public void SendItemSpatialSettlement(ItemSpatialStateData state)
    {
        if (state == null) return;
        SendPacketToServer(new ServerboundItemSpatialSettlementPacket { State = state },
            DeliveryMethod.ReliableOrdered);
    }

    public void SendItemTrainWakeWitness(ServerboundItemTrainWakeWitnessPacket packet)
    {
        if (packet == null) return;
        SendPacketToServer(packet, DeliveryMethod.ReliableOrdered);
    }
}
