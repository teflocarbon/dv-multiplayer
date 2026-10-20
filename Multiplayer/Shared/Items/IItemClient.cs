using System.Collections.Generic;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Items;

/// <summary>
///     What Shared item code sends to the server as the local player (contracts.md, "Area
///     interfaces"). <c>NetworkClient</c> implements it, in <c>Client/Session/NetworkClient.Items.cs</c>.
///     The local player's id and the other players' avatars are on <see cref="ILocalPlayerSession"/>.
/// </summary>
public interface IItemClient
{
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
