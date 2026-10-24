using System;
using System.Collections.Generic;
using Multiplayer.Client.Items.LostAndFound;
using Multiplayer.Client.Items.Requests;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Serverbound;

namespace Multiplayer.Client.Session;

// The host's player's items. Its own grabs, drops, throws and leases are the server's world
// already and go to the server part directly (NetworkedItemManager.ServerPart), so all this
// carries is its requests (a recall, a Lost and Found collection, a container's) and lists:
// handled in-process (NetworkServer.RegisterLocalItems), answered to the host's player's peer.
public partial class LocalPlayerSession : IItemClient
{
    public event Action<ClientboundItemResultPacket> ItemResultReceived;
    public event Action<ClientboundItemListPacket> ItemListReceived;

    partial void RegisterItems()
    {
        Handle<ClientboundItemResultPacket>(packet =>
        {
            ItemExchange.Receive(packet);
            ItemResultReceived?.Invoke(packet);
        });
        Handle<ClientboundItemListPacket>(packet =>
        {
            ItemExchange.Receive(packet);
            ItemListReceived?.Invoke(packet);
        });
        Handle<ClientboundStashedSoundPacket>(global::Multiplayer.Shared.Items.Inventory.HeardStashedSounds.Receive);
    }

    public uint SendItemRequest(ItemRequest request)
    {
        if (request == null || request.ItemNetId == 0 && request.ItemHandle == 0)
            return 0;
        ItemExchange.Number(request);
        Submit(new ServerboundItemRequestPacket { Request = request });
        return request.RequestId;
    }

    public uint RequestItemList(ItemListKind kind, ushort shellNetId = 0, uint container = 0)
    {
        uint requestId = ItemExchange.NextRequestId();
        Submit(new ServerboundItemListRequestPacket
        {
            RequestId = requestId,
            Kind = kind,
            ShellNetId = shellNetId,
            Container = container
        });
        return requestId;
    }

    IEnumerable<Dictionary<string, object>> IItemClient.LostAndFoundDebugSnapshot() => LostAndFoundList.DebugSnapshot();

    // A remote client's item stream. The host's items never take this path (each caller is a
    // remote client's); if one did, Submit names the request the server doesn't take in-process.
    IItemManagerClientPart IItemClient.CreateItemManagerPart(NetworkedItemManager manager) => null;
    INetworkedItemClientPart IItemClient.CreateItemPart(NetworkedItem item) => null;
    void IItemClient.SendWorldItemProjectionAck(ushort netId, uint revision, bool projected) =>
        Submit(new ServerboundWorldItemProjectionAckPacket { ItemNetId = netId, AuthorityRevision = revision, Projected = projected });
    void IItemClient.SendItemSpatialSample(ItemSpatialStateData state) =>
        Submit(new ServerboundItemSpatialSamplePacket { State = state });
    void IItemClient.SendItemSpatialSettlement(ItemSpatialStateData state) =>
        Submit(new ServerboundItemSpatialSettlementPacket { State = state });
    void IItemClient.SendItemTrainWakeWitness(ServerboundItemTrainWakeWitnessPacket packet) => Submit(packet);
    void IItemClient.SendStashedCassette(ushort netId, int track, long seek, bool ended) =>
        Submit(new ServerboundStashedCassettePacket { NetId = netId, Track = track, Seek = seek, Ended = ended });
}
