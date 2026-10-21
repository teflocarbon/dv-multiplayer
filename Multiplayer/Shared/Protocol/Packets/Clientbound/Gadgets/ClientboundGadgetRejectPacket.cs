namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;

/// <summary>The server refused one of this player's gadget requests (gadgets.md, "Requests"), and why.</summary>
public class ClientboundGadgetRejectPacket
{
    public uint OperationId { get; set; }
    public ushort ItemNetId { get; set; }
    public string Reason { get; set; } = string.Empty;
}
