namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// A player edits an installed label's text with the label maker, or deletes the label from its popup
/// (PR 9). The text goes out as the change log's <c>TextSet</c>; a deleted label comes off, and its item
/// is replaced by the server.
/// </summary>
public class ServerboundGadgetTextPacket
{
    public uint OperationId { get; set; }
    public ushort ItemNetId { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool Remove { get; set; }
}
