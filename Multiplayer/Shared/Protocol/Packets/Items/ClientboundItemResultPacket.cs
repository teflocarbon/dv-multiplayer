using Multiplayer.Core.Items.LiveState;

namespace Multiplayer.Shared.Protocol.Packets.Items;

/// <summary>
/// The answer to one <see cref="ServerboundItemRequestPacket"/>: accepted (<see cref="Refusal"/>
/// <see cref="ItemRefusal.None"/>) or why not, and the revision it made. What changed arrives as
/// updates and lists, not here.
/// </summary>
public class ClientboundItemResultPacket
{
    public uint RequestId { get; set; }
    public ItemRefusal Refusal { get; set; }
    public string Detail { get; set; } = string.Empty;
    public uint Revision { get; set; }
    /// <summary>The live item the request ended with, if any: a withdrawn item's new object.</summary>
    public ushort ItemNetId { get; set; }

    public bool Accepted => Refusal == ItemRefusal.None;

    /// <summary>
    /// A copy to keep past the handler: the client reads every answer into one packet object (a
    /// reusable subscription), so a kept packet reads the next answer.
    /// </summary>
    public ClientboundItemResultPacket Clone() => (ClientboundItemResultPacket)MemberwiseClone();
}
