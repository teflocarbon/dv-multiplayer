using System;
using System.Collections.Generic;
using Multiplayer.Client.Items;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Protocol.Packets.Serverbound;

namespace Multiplayer.Client.Session;

// The host's player's items. Its own grabs, drops, throws, recalls and leases are the server's
// world already and go to the server part directly (NetworkedItemManager.ServerPart), so all this
// carries is the Lost and Found screen and the container UI: requests handled in-process
// (NetworkServer.RegisterLocalItems) and the answers the server sends the host's player.
public partial class LocalPlayerSession : IItemClient
{
    public event Action<ClientboundLostItemRetrieveResultPacket> LostItemRetrieveCompleted;
    public event Action<ClientboundContainerViewPacket> ContainerViewReceived;
    public event Action<ClientboundContainerMutationResultPacket> ContainerMutationCompleted;

    partial void RegisterItems()
    {
        Handle<ClientboundLostItemsSnapshotPacket>(LostAndFoundExchange.ApplySnapshot);
        Handle<ClientboundOwnedItemsPacket>(OwnedItemsList.Apply);
        Handle<ClientboundLostItemRetrieveResultPacket>(packet =>
        {
            LostAndFoundExchange.ReportRetrieveResult(packet);
            LostItemRetrieveCompleted?.Invoke(packet);
        });
        Handle<ClientboundContainerViewPacket>(packet => ContainerViewReceived?.Invoke(packet));
        Handle<ClientboundContainerMutationResultPacket>(packet => ContainerMutationCompleted?.Invoke(packet));
    }

    public void RequestLostItems(uint requestId) =>
        Submit(new ServerboundLostItemsRequestPacket { RequestId = requestId });

    public void RequestOwnedItems(uint requestId) =>
        Submit(new ServerboundOwnedItemsRequestPacket { RequestId = requestId });

    public void RequestLostItemRetrieval(uint requestId, uint lostHandle, ushort currentNetId,
        uint expectedRevision, int requestedSlot = -1) =>
        Submit(LostAndFoundExchange.Retrieval(requestId, lostHandle, currentNetId, expectedRevision, requestedSlot));

    public void RequestContainerView(uint requestId, ushort shellNetId, uint containerHandle = 0, int offset = 0,
        byte count = 64) =>
        Submit(new ServerboundContainerBrowsePacket
        {
            RequestId = requestId,
            ShellNetId = shellNetId,
            ContainerHandle = containerHandle,
            Offset = offset,
            Count = count
        });

    public void RequestContainerMutation(ServerboundContainerMutationPacket packet)
    {
        if (packet != null)
            Submit(packet);
    }

    IEnumerable<Dictionary<string, object>> IItemClient.LostAndFoundDebugSnapshot() => LostAndFoundList.DebugSnapshot();

    // A remote client's item stream. The host's items never take this path (each caller is a
    // remote client's); if one did, Submit names the request the server doesn't take in-process.
    IItemManagerClientPart IItemClient.CreateItemManagerPart(NetworkedItemManager manager) => null;
    INetworkedItemClientPart IItemClient.CreateItemPart(NetworkedItem item) => null;
    void IItemClient.SendItemUpdatePacket(ItemUpdateData updateData) =>
        Submit(new CommonItemUpdatePacket { ItemData = updateData });
    void IItemClient.SendWorldItemProjectionAck(ushort netId, uint revision, bool projected) =>
        Submit(new ServerboundWorldItemProjectionAckPacket { ItemNetId = netId, AuthorityRevision = revision, Projected = projected });
    void IItemClient.SendItemRecall(ushort itemNetId, uint expectedRevision, int requestedSlot) =>
        Submit(new ServerboundItemRecallPacket { ItemNetId = itemNetId, ExpectedRevision = expectedRevision, RequestedSlot = requestedSlot });
    void IItemClient.SendItemBelowMapReport(ushort itemNetId, float surfaceHeight) =>
        Submit(new ServerboundItemBelowMapPacket { ItemNetId = itemNetId, SurfaceHeight = surfaceHeight });
    void IItemClient.SendItemSpatialSample(ItemSpatialStateData state) =>
        Submit(new ServerboundItemSpatialSamplePacket { State = state });
    void IItemClient.SendItemSpatialSettlement(ItemSpatialStateData state) =>
        Submit(new ServerboundItemSpatialSettlementPacket { State = state });
    void IItemClient.SendItemTrainWakeWitness(ServerboundItemTrainWakeWitnessPacket packet) => Submit(packet);
}
