using System.Collections.Generic;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Items;

/// <summary>
/// Item changes for one recipient, built for it (its relation to each item, and the claim only if
/// it's the owner) and sent by interest (docs/design/items/wire.md, "The update"). The only item
/// update packet: a player's own move goes to the server as a request.
/// </summary>
public class ClientboundItemUpdatesPacket
{
    public List<ItemUpdate> Updates { get; set; }
}
