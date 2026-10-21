namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// A player soldering a gadget (gadgets.md, "Soldering"): the units their game has reached, absolute,
/// sent at 5 Hz while they solder and once when they stop. Their game solders at once; the server
/// clamps the units to what the time since allows and its <c>Soldered</c> entry is the truth.
/// </summary>
public class ServerboundGadgetSolderPacket
{
    public uint OperationId { get; set; }
    public ushort ItemNetId { get; set; }
    public int Units { get; set; }
}
