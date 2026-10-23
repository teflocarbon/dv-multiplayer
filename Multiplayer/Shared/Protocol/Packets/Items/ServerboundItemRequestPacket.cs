using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Items;

/// <summary>
/// One item request (<see cref="ItemRequest"/>): every change a player makes to an item, and every
/// action on a row. The server answers it with one <see cref="ClientboundItemResultPacket"/>, and a
/// repeat of its <c>RequestId</c> with the same answer (docs/design/items/wire.md).
/// </summary>
public class ServerboundItemRequestPacket
{
    public ItemRequest Request { get; set; }
}
