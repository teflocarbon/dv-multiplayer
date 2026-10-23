using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Items;

/// <summary>
/// Asks for one list (<see cref="ClientboundItemListPacket"/>). A container is named by the NetId
/// of its shell (a toolbox on the ground) or by a handle (a container row, or a list's own).
/// </summary>
public class ServerboundItemListRequestPacket
{
    public uint RequestId { get; set; }
    public ItemListKind Kind { get; set; }
    public ushort ShellNetId { get; set; }
    public uint Container { get; set; }
}
