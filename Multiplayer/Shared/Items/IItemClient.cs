using System;
using System.Collections.Generic;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Items;

/// <summary>
///     What item code sends to the server as the local player, and the answers to its Lost and
///     Found and container screens (contracts.md, "Area interfaces"). A remote client's
///     <c>NetworkClient</c> implements it (<c>Client/Session/NetworkClient.Items.cs</c>), and so does
///     the host's <c>LocalPlayerSession</c> (<c>LocalPlayerSession.Items.cs</c>), whose own items go
///     to the server directly: on the host only the screens' requests and answers are used.
///     The local player's id and the other players' avatars are on <see cref="ILocalPlayerSession"/>.
/// </summary>
public interface IItemClient
{
    // The Lost and Found screen and the container UI: the requests, and the server's answers.
    event Action<ClientboundLostItemRetrieveResultPacket> LostItemRetrieveCompleted;
    event Action<ClientboundContainerViewPacket> ContainerViewReceived;
    event Action<ClientboundContainerMutationResultPacket> ContainerMutationCompleted;
    void RequestLostItems(uint requestId);
    void RequestContainerView(uint requestId, ushort shellNetId, uint containerHandle = 0, int offset = 0,
        byte count = 64);
    void RequestContainerMutation(ServerboundContainerMutationPacket packet);

    void SendItemUpdatePacket(ItemUpdateData updateData);
    void SendWorldItemProjectionAck(ushort netId, uint revision, bool projected);
    void SendItemAdoption(ItemAdoptionRequestData request);
    void SendItemRecall(ushort itemNetId, uint expectedRevision, int requestedSlot);
    void SendItemBelowMapReport(ushort itemNetId, float surfaceHeight);
    void SendItemSpatialSample(ItemSpatialStateData state);
    void SendItemSpatialSettlement(ItemSpatialStateData state);
    void SendItemTrainWakeWitness(ServerboundItemTrainWakeWitnessPacket packet);

    /// <summary>The remote client's part of a new item manager (never asked for on the host).</summary>
    IItemManagerClientPart CreateItemManagerPart(NetworkedItemManager manager);

    /// <summary>The remote client's half of a new item (never asked for on the host).</summary>
    INetworkedItemClientPart CreateItemPart(NetworkedItem item);

    void RequestLostItemRetrieval(uint requestId, uint lostHandle, ushort currentNetId,
        uint expectedRevision, int requestedSlot = -1);

    /// <summary>The local player's Lost and Found list as the server last sent it, for the in-game overlay.</summary>
    IEnumerable<Dictionary<string, object>> LostAndFoundDebugSnapshot();
}

public static class ItemClient
{
    /// <summary>This game's local player session's item side, or null when there's none.</summary>
    public static IItemClient Current => NetworkLifecycle.Instance?.LocalSession as IItemClient;
}
