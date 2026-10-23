using System;
using System.Collections.Generic;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Items;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Items.LiveState;

/// <summary>
///     What item code sends to the server as the local player, and the server's answers
///     (contracts.md, "Area interfaces"; the protocol is docs/design/items/wire.md). A remote
///     client's <c>NetworkClient</c> implements it (<c>Client/Session/NetworkClient.Items.cs</c>), and
///     so does the host's <c>LocalPlayerSession</c> (<c>LocalPlayerSession.Items.cs</c>), which submits
///     its requests to the server in-process. The host's player's own grabs and drops are the
///     server's world already and never become requests. The local player's id and the other
///     players' avatars are on <see cref="ILocalPlayerSession"/>.
/// </summary>
public interface IItemClient
{
    /// <summary>The answer to one of this game's requests (its <c>RequestId</c>).</summary>
    event Action<ClientboundItemResultPacket> ItemResultReceived;
    /// <summary>A list this game asked for, or one the server sent unasked (request 0).</summary>
    event Action<ClientboundItemListPacket> ItemListReceived;

    /// <summary>Sends one request, numbered with the next request id if it has none; returns its id (0 if not sent).</summary>
    uint SendItemRequest(ItemRequest request);
    /// <summary>Asks for one list: Lost and Found, owned items, or a container by its shell or handle. Returns the request id.</summary>
    uint RequestItemList(ItemListKind kind, ushort shellNetId = 0, uint container = 0);

    void SendWorldItemProjectionAck(ushort netId, uint revision, bool projected);
    void SendItemSpatialSample(ItemSpatialStateData state);
    void SendItemSpatialSettlement(ItemSpatialStateData state);
    void SendItemTrainWakeWitness(ServerboundItemTrainWakeWitnessPacket packet);

    /// <summary>The remote client's part of a new item manager (never asked for on the host).</summary>
    IItemManagerClientPart CreateItemManagerPart(NetworkedItemManager manager);

    /// <summary>The remote client's half of a new item (never asked for on the host).</summary>
    INetworkedItemClientPart CreateItemPart(NetworkedItem item);

    /// <summary>The local player's Lost and Found list as the server last sent it, for the in-game overlay.</summary>
    IEnumerable<Dictionary<string, object>> LostAndFoundDebugSnapshot();
}

public static class ItemClient
{
    /// <summary>This game's local player session's item side, or null when there's none.</summary>
    public static IItemClient Current => NetworkLifecycle.Instance?.LocalSession as IItemClient;
}
