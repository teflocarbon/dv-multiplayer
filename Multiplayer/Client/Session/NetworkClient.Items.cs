using DV;
using DV.InventorySystem;
using LiteNetLib;
using Multiplayer.Client.Items;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Client.Session;

// NetworkClient's items handlers and sends (see docs/design/code-layout.md).
public partial class NetworkClient : IItemClient
{
    private void OnCommonItemsBulkUpdatePacket(CommonItemsBulkUpdatePacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client);
        LogDebug(() => $"OnCommonItemsBulkUpdatePacket({packet?.Items?.Count})");

        LogDebug(() =>
        {
            string debug = "";

            foreach (var item in packet?.Items)
            {
                debug += "UpdateType: " + item?.UpdateType + "\r\n";
                debug += "itemNetId: " + item?.ItemNetId + "\r\n";
                debug += "PrefabName: " + item?.PrefabName + "\r\n";
                debug += "Equipped: " + item?.ItemState + "\r\n";
                debug += "Position: " + item?.ItemPosition + "\r\n";
                debug += "Rotation: " + item?.ItemRotation + "\r\n";
                debug += "ThrowDirection: " + item?.ThrowDirection + "\r\n";
                debug += "Player: " + item?.PlayerId + "\r\n";
                debug += "CarNetId: " + item?.CarNetId + "\r\n";
                debug += "AttachedFront: " + item?.AttachedFront + "\r\n";

                debug += $"States: {item?.States?.Count}\r\n";

                if (item.States != null)
                    foreach (var state in item?.States)
                        debug += "\t" + state.Key + ": " + state.Value + "\r\n";
                else
                    debug += "\r\n";
            }

            return debug;
        });

        ClientItemManager.Instance?.ReceiveSnapshots(packet.Items);
    }

    private void OnCommonItemUpdatePacket(CommonItemUpdatePacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client, "Item", packet?.ItemData?.ItemNetId.ToString());
        LogDebug(() => $"OnCommonItemUpdatePacket()");

        LogDebug(() =>
        {
            string debug = "";

            var item = packet.ItemData;
            debug += "UpdateType: " + item?.UpdateType + "\r\n";
            debug += "itemNetId: " + item?.ItemNetId + "\r\n";
            debug += "PrefabName: " + item?.PrefabName + "\r\n";
            debug += "Equipped: " + item?.ItemState + "\r\n";
            debug += "Position: " + item?.ItemPosition + "\r\n";
            debug += "Rotation: " + item?.ItemRotation + "\r\n";
            debug += "ThrowDirection: " + item?.ThrowDirection + "\r\n";
            debug += "Player: " + item?.PlayerId + "\r\n";
            debug += "CarNetId: " + item?.CarNetId + "\r\n";
            debug += "AttachedFront: " + item?.AttachedFront + "\r\n";

            debug += $"States: {item?.States?.Count}\r\n";

            if (item.States != null)
                foreach (var state in item?.States)
                    debug += "\t" + state.Key + ": " + state.Value + "\r\n";
            else
                debug += "\r\n";

            return debug;
        });

        // Create and Destroy are representation-lifecycle operations. The bulk packet path
        // already routes them through NetworkedItemManager, but the single-item path used to
        // bypass it and call ReceiveSnapshot directly. That left Destroyed objects registered,
        // inventoried, and visible even though the packet had arrived successfully.
        if (packet.ItemData.UpdateType.HasAnyFlag(
                ItemUpdateData.ItemUpdateType.Create |
                ItemUpdateData.ItemUpdateType.Destroy))
        {
            ClientItemManager.Instance?.ReceiveSnapshots(
                new List<ItemUpdateData> { packet.ItemData });
            return;
        }

        if (!NetworkedItem.TryGet(packet.ItemData.ItemNetId, out NetworkedItem networkedItem))
        {
            DebugTrace.Validation("item", "Item", packet.ItemData.ItemNetId.ToString(), false, "unknown-network-entity", DebugRuntimeSide.Client);
            LogWarning($"OnCommonItemUpdatePacket() Failed to find networked item for netId {packet.ItemData.ItemNetId}");
            return;
        }

        if (packet.ItemData.MoveCause.HasValue)
            ItemChangeCauses.OnClientSnapshot(networkedItem, packet.ItemData, PlayerId);
        networkedItem.ReceiveSnapshot(packet.ItemData);
    }

    private void OnClientboundItemAdoptionPacket(ClientboundItemAdoptionPacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client, "Item", null);
        ClientItemManager.Instance?.ApplyItemAdoptionResult(packet.Result);
    }

    private void OnClientboundItemRecallResultPacket(ClientboundItemRecallResultPacket packet)
    {
        Log($"[LostAndFound Recall] Recall result received: netId={packet.ItemNetId}, " +
            $"operation={packet.OperationId}, " +
            $"accepted={packet.Accepted}, revision={packet.AuthorityRevision}, " +
            $"reason={packet.RejectionReason ?? string.Empty}");
        DebugRuntime.Publish("item", packet.Accepted ? "item.recall-confirmed" : "item.recall-rejected",
            DebugRuntimeSide.Client, packet.Accepted ? DebugSeverity.Info : DebugSeverity.Warning,
            "Item", packet.ItemNetId.ToString(), new()
            {
                ["authorityRevision"] = packet.AuthorityRevision,
                ["rejectionReason"] = packet.RejectionReason ?? string.Empty
            });
        if (NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem item))
            item?.CompleteLocalRecallPreparation(packet.OperationId, packet.Accepted);
    }

    private void OnClientboundItemRecallPreparePacket(ClientboundItemRecallPreparePacket packet)
    {
        string failureReason = string.Empty;
        bool succeeded = NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem item) &&
            item != null && item.TryPrepareLocalRecall(packet.OperationId, packet.RequestedSlot,
                out failureReason);
        if (item == null)
            failureReason = "missing-local-item-representation";
        SendPacketToServer(new ServerboundItemRecallPreparedPacket
        {
            OperationId = packet.OperationId,
            ItemNetId = packet.ItemNetId,
            Succeeded = succeeded,
            FailureReason = failureReason ?? string.Empty
        }, DeliveryMethod.ReliableOrdered);
    }

    private void OnClientboundLostItemsSnapshotPacket(ClientboundLostItemsSnapshotPacket packet)
    {
        int count = new[]
        {
            packet.Handles?.Length ?? 0,
            packet.NetIds?.Length ?? 0,
            packet.Revisions?.Length ?? 0,
            packet.PrefabNames?.Length ?? 0,
            packet.DisplayNames?.Length ?? 0,
            packet.Reasons?.Length ?? 0,
            packet.LostUtcTicks?.Length ?? 0
        }.Min();
        LostItemData[] items = new LostItemData[count];
        for (int i = 0; i < count; i++)
            items[i] = new LostItemData
            {
                Handle = packet.Handles[i],
                NetId = packet.NetIds[i],
                Revision = packet.Revisions[i],
                PrefabName = packet.PrefabNames[i],
                DisplayName = packet.DisplayNames[i],
                Reason = packet.Reasons[i],
                LostUtcTicks = packet.LostUtcTicks[i]
            };
        LostAndFoundList.Apply(items);
    }

    private void OnClientboundLostItemRetrieveResultPacket(ClientboundLostItemRetrieveResultPacket packet)
    {
        Log($"[LostAndFound Recall] Lost item retrieval result received: request={packet.RequestId}, " +
            $"handle={packet.LostHandle}, accepted={packet.Accepted}, revision={packet.AuthorityRevision}, " +
            $"reason={packet.RejectionReason ?? string.Empty}");
        DebugRuntime.Publish("item", packet.Accepted ? "item.lost-and-found-recall-confirmed" :
            "item.lost-and-found-recall-rejected", DebugRuntimeSide.Client,
            packet.Accepted ? DebugSeverity.Info : DebugSeverity.Warning,
            "LostItem", packet.LostHandle.ToString(), new()
            {
                ["requestId"] = packet.RequestId,
                ["lostHandle"] = packet.LostHandle,
                ["authorityRevision"] = packet.AuthorityRevision,
                ["rejectionReason"] = packet.RejectionReason ?? string.Empty
            });
        LostItemRetrieveCompleted?.Invoke(packet);
    }

    private void OnClientboundContainerViewPacket(ClientboundContainerViewPacket packet)
    {
        ContainerViewReceived?.Invoke(packet);
    }

    private void OnClientboundContainerMutationResultPacket(
        ClientboundContainerMutationResultPacket packet)
    {
        ContainerMutationCompleted?.Invoke(packet);
    }

    //public void SendItemsBulkUpdatePacket(List<ItemUpdateData> items)
    //{
    //    Log($"Sending CommonItemsBulkUpdatePacket with {items.Count()} items");

    //    SendNetSerializablePacketToServer(new CommonItemsBulkUpdatePacket { Items = items },
    //            DeliveryMethod.ReliableOrdered);
    //}

    IItemManagerClientPart IItemClient.CreateItemManagerPart(NetworkedItemManager manager) => new ClientItemManager(manager);

    INetworkedItemClientPart IItemClient.CreateItemPart(NetworkedItem item) => new NetworkedItemClient(item);

    public void SendItemUpdatePacket(ItemUpdateData updateData)
    {
        if (updateData == null || updateData.ItemNetId == 0)
        {
            LogWarning($"Blocked local item snapshot with no host-issued network ID. UpdateType: {updateData?.UpdateType}, prefab: {updateData?.PrefabName}");
            DebugRuntime.Publish("item", "item.zero-id-send-blocked", DebugRuntimeSide.Client, DebugSeverity.Error,
                "Item", "0", DebugTrace.ItemSnapshotData(updateData));
            return;
        }

        DebugRuntime.Publish("item", "item.packet-send-requested", DebugRuntimeSide.Client, entityType: "Item",
            entityId: updateData?.ItemNetId.ToString(), data: DebugTrace.ItemSnapshotData(updateData));
        LogDebug(()=>
        {
            string debug = $"SendItemUpdatePacket() UpdateType: {updateData?.UpdateType}, itemNetId: {updateData?.ItemNetId}, PrefabName: {updateData?.PrefabName}, Equipped: {updateData?.ItemState}, Position: {updateData?.ItemPosition}, Rotation: {updateData?.ItemRotation}, ThrowDirection: {updateData?.ThrowDirection}, Player: {updateData?.PlayerId}, CarNetId: {updateData?.CarNetId}, AttachedFront: {updateData?.AttachedFront}";
            debug += $"\r\nStates: {updateData?.States?.Count}";
            if (updateData.States != null)
                foreach (var state in updateData?.States)
                    debug += $"\r\n\t{state.Key}: {state.Value}";
            return debug;
        });

        SendPacketToServer(new CommonItemUpdatePacket { ItemData = updateData },
                DeliveryMethod.ReliableOrdered);
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

    public void SendItemAdoption(ItemAdoptionRequestData request)
    {
        // TEMPORARY compatibility only. This is not a general client creation API.
        SendPacketToServer(new ServerboundItemAdoptionPacket
        {
            Request = request
        }, DeliveryMethod.ReliableOrdered);
    }

    public void RequestLostItems(uint requestId)
    {
        SendPacketToServer(new ServerboundLostItemsRequestPacket { RequestId = requestId },
            DeliveryMethod.ReliableOrdered);
    }

    public void RequestContainerView(uint requestId, ushort shellNetId,
        uint containerHandle = 0, int offset = 0, byte count = 64)
    {
        SendPacketToServer(new ServerboundContainerBrowsePacket
        {
            RequestId = requestId,
            ShellNetId = shellNetId,
            ContainerHandle = containerHandle,
            Offset = offset,
            Count = count
        }, DeliveryMethod.ReliableOrdered);
    }

    public void RequestContainerMutation(ServerboundContainerMutationPacket packet)
    {
        if (packet == null) return;
        SendPacketToServer(packet, DeliveryMethod.ReliableOrdered);
    }

    public void RequestLostItemRetrieval(uint requestId, uint lostHandle, ushort currentNetId,
        uint expectedRevision, int requestedSlot = -1)
    {
        Inventory inventory = Inventory.Instance;
        int capacity = inventory?.Capacity ?? 0;
        int existingItemSlot = -1;
        if (NetworkedItem.TryGet(currentNetId, out NetworkedItem existingItem) && existingItem != null)
            existingItemSlot = inventory?.IndexOf(existingItem.gameObject) ?? -1;
        List<int> occupied = new();
        for (int slot = 0; slot < capacity; slot++)
            if (inventory.PeekItemAtSlot(slot, true) != null)
                occupied.Add(slot);
        Log($"[LostAndFound Recall] Preparing retrieval packet: request={requestId}, " +
            $"handle={lostHandle}, currentNetId={currentNetId}, " +
            $"revision={expectedRevision}, requestedSlot={requestedSlot}, existingSlot={existingItemSlot}, " +
            $"capacity={capacity}, occupied=[{string.Join(",", occupied)}]");
        SendPacketToServer(new ServerboundLostItemRetrievePacket
        {
            RequestId = requestId,
            LostHandle = lostHandle,
            ExpectedRevision = expectedRevision,
            RequestedSlot = requestedSlot,
            ExistingItemSlot = existingItemSlot,
            InventoryCapacity = capacity,
            OccupiedSlots = occupied.ToArray()
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
        NetworkedItemManager.Instance?.ReceiveItemSpatialLease(packet);

    private void OnClientboundItemSpatialSamplePacket(ClientboundItemSpatialSamplePacket packet)
    {
        if (packet?.State != null)
            NetworkedItemManager.Instance?.ReceiveItemSpatialSample(packet.State);
    }

    private void OnClientboundItemSpatialCommitPacket(ClientboundItemSpatialCommitPacket packet) =>
        NetworkedItemManager.Instance?.ReceiveItemSpatialCommit(packet);

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

    public void SendItemRecall(ushort itemNetId, uint expectedRevision, int requestedSlot)
    {
        SendPacketToServer(new ServerboundItemRecallPacket
        {
            ItemNetId = itemNetId,
            ExpectedRevision = expectedRevision,
            RequestedSlot = requestedSlot
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendItemBelowMapReport(ushort itemNetId, float surfaceHeight)
    {
        SendPacketToServer(new ServerboundItemBelowMapPacket { ItemNetId = itemNetId, SurfaceHeight = surfaceHeight },
            DeliveryMethod.ReliableOrdered);
    }

    public void SendMoneyItemDeposit(ushort itemNetId)
    {
        SendPacketToServer(new ServerboundMoneyItemDepositPacket { ItemNetId = itemNetId },
            DeliveryMethod.ReliableOrdered);
    }
}
