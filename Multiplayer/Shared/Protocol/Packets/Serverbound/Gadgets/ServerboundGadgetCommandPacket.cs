namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// A player pressed a button on an installed gadget (gadgets.md, "Gadget state"): a
/// <see cref="Multiplayer.Shared.Gadgets.GadgetCommand"/>. The player's own game already shows its
/// effect; the server runs it and its running state broadcast confirms it or puts it back.
/// </summary>
public class ServerboundGadgetCommandPacket
{
    public ushort ItemNetId { get; set; }
    public uint OperationId { get; set; }
    public byte Command { get; set; }
}
