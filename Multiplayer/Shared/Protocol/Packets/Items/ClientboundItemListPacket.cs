using Multiplayer.Core.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Packets.Items;

/// <summary>
/// A list of rows (docs/design/items/wire.md, "One list"): the recipient's Lost and Found, its
/// owned items, or a container's slots. Sent for a <see cref="ServerboundItemListRequestPacket"/>
/// (its <c>RequestId</c>), and unasked (request 0) when the recipient's Lost and Found changes.
/// </summary>
public class ClientboundItemListPacket
{
    public uint RequestId { get; set; }
    public ItemListKind Kind { get; set; }
    /// <summary>A container that can't be opened says why; the other lists are never refused.</summary>
    public ItemRefusal Refusal { get; set; }
    public string Detail { get; set; } = string.Empty;
    /// <summary>A container list's own handle, its revision and its slot count.</summary>
    public uint Container { get; set; }
    public uint Revision { get; set; }
    public int Capacity { get; set; }
    public ItemRow[] Rows { get; set; } = System.Array.Empty<ItemRow>();

    public bool Accepted => Refusal == ItemRefusal.None;

    /// <summary>
    /// A copy to keep past the handler, its rows with it: the client reads every list into one packet
    /// object (a reusable subscription), so a kept packet reads the next list, of any kind.
    /// </summary>
    public ClientboundItemListPacket Clone()
    {
        ClientboundItemListPacket copy = (ClientboundItemListPacket)MemberwiseClone();
        copy.Rows = Rows != null ? (ItemRow[])Rows.Clone() : System.Array.Empty<ItemRow>();
        return copy;
    }
}
