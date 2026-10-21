namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// A player takes an installed gadget off (gadgets.md, "Requests"): with the hammer, by hand, or by
/// telegrab (GadgetBase.GadgetRemovalMethod). Answered with the change log's <c>Removed</c> entry or a
/// rejection.
/// </summary>
public class ServerboundGadgetRemovePacket
{
    public uint OperationId { get; set; }
    public ushort ItemNetId { get; set; }
    public byte Method { get; set; }
}
